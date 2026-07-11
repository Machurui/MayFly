using System.Diagnostics;
using System.Formats.Tar;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Engines;
using Microsoft.Extensions.Logging;

namespace MayFly.Provisioner.Docker;

public sealed class DockerProvisioner : IDockerProvisioner
{
    private const string SidecarImage = "alpine/socat:1.8.0.0";

    /// <summary>Internal-only bridge network: user DB containers live here; no gateway → no egress.</summary>
    private const string UserNetwork = "mayfly-users";

    /// <summary>Normal bridge network: socat sidecar publishes the host port from here.</summary>
    private const string IngressNetwork = "mayfly-ingress";

    private readonly IDockerClient _docker;
    private readonly IPortAllocator _ports;
    private readonly IVolumeProvisioner _volumes;
    private readonly IReadOnlyDictionary<string, IEngineProvider> _engines;
    private readonly ILogger<DockerProvisioner> _log;

    public DockerProvisioner(
        IDockerClient docker, IPortAllocator ports, IVolumeProvisioner volumes,
        IEnumerable<IEngineProvider> providers,
        ILogger<DockerProvisioner> log)
    {
        _docker = docker;
        _ports = ports;
        _volumes = volumes;
        _engines = providers.ToDictionary(p => p.EngineId);
        _log = log;
    }

    public async Task<CreateInstanceResult> CreateAsync(CreateInstanceRequest req, CancellationToken ct)
    {
        if (!_engines.TryGetValue(req.Engine, out var provider))
            throw new InvalidOperationException(
                $"Unknown engine '{req.Engine}'. Registered engines: {string.Join(", ", _engines.Keys)}");

        var creds = provider.GenerateCredentials();
        var setup = provider.BuildSetup(creds, req.InitialData);

        var id = Guid.NewGuid().ToString("N")[..16];
        var name = $"mayfly-pg-{id}";
        var port = _ports.Allocate();
        string? volume = null;
        string? initVolumeName = null;
        string? writerId = null;
        string? containerId = null;
        string? sidecarId = null;

        try
        {
            await EnsureImageAsync(provider.Image, ct);
            await EnsureSidecarImageAsync(ct);
            await EnsureNetworksAsync(ct);
            volume = await _volumes.CreateAsync(id, req.StorageMb, ct);

            if (provider.UsesInitVolume)
            {
                // Create a named volume for init scripts. ReadonlyRootfs prevents
                // ExtractArchiveToContainerAsync from writing to the container's writable layer
                // directly, so we populate the volume via a temporary writer container instead.
                initVolumeName = $"mayfly-init-{id}";
                await _docker.Volumes.CreateAsync(
                    new VolumesCreateParameters
                    {
                        Name = initVolumeName,
                        Labels = new Dictionary<string, string> { ["mayfly.instance"] = id }
                    }, ct);

                // Temporary writer: engine image with overridden entrypoint so the init
                // scripts entrypoint does NOT run. We start it just long enough to let Docker
                // resolve the volume mount, then extract the tar into it.
                var writerName = $"mayfly-initwriter-{id}";
                var writerCreate = await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
                {
                    Image = provider.Image,
                    Name = writerName,
                    Entrypoint = new List<string> { "sh" },
                    Cmd = new List<string> { "-c", "sleep infinity" },
                    Labels = new Dictionary<string, string>
                    {
                        ["mayfly.instance"] = id,
                        ["mayfly.role"] = "writer"
                    },
                    HostConfig = new HostConfig
                    {
                        Mounts = new List<Mount>
                        {
                            new() { Type = "volume", Source = initVolumeName, Target = "/docker-entrypoint-initdb.d" }
                        }
                    }
                }, ct);
                writerId = writerCreate.ID;

                // Start writer so Docker mounts the volume — extractions go into the volume.
                await _docker.Containers.StartContainerAsync(writerId, new ContainerStartParameters(), ct);
                await using var tarStream = BuildInitScriptsTar(setup.InitScripts);
                await _docker.Containers.ExtractArchiveToContainerAsync(
                    writerId,
                    new CopyToContainerParameters { Path = "/docker-entrypoint-initdb.d" },
                    tarStream,
                    ct);
                await _docker.Containers.RemoveContainerAsync(
                    writerId, new ContainerRemoveParameters { Force = true }, ct);
                writerId = null;
            }

            // Build the generic container topology: mounts, networking, restart policy.
            // Engine-specific hardening (rootfs/tmpfs/caps/limits) is layered on by the provider.
            var mounts = new List<Mount>
            {
                new() { Type = "volume", Source = volume, Target = provider.DataDirectory }
            };
            if (provider.UsesInitVolume)
            {
                mounts.Add(new() { Type = "volume", Source = initVolumeName, Target = "/docker-entrypoint-initdb.d", ReadOnly = true });
            }

            var hc = new HostConfig
            {
                Mounts = mounts,
                NetworkMode = UserNetwork,
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No }
            };
            provider.ApplyHardening(hc);

            // Create DB container on the internal user network (no published ports).
            var create = await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = provider.Image,
                Name = name,
                Hostname = name,
                Labels = new Dictionary<string, string>
                {
                    ["mayfly.instance"] = id,
                    ["mayfly.role"] = "db"
                },
                Env = new List<string>(provider.BuildEnv(creds)),
                HostConfig = hc
            }, ct);
            containerId = create.ID;

            await _docker.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct);

            // Create socat sidecar: listens on the public host port and forwards to the DB by name.
            // Create on mayfly-ingress first (for the host port binding), then connect to mayfly-users.
            var enginePort = provider.Port;
            var sidecarCreate = await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = SidecarImage,
                Name = $"mayfly-sidecar-{id}",
                Labels = new Dictionary<string, string>
                {
                    ["mayfly.instance"] = id,
                    ["mayfly.role"] = "sidecar"
                },
                Cmd = new List<string> { "-d", $"TCP-LISTEN:{enginePort},fork,reuseaddr", $"TCP:{name}:{enginePort}" },
                ExposedPorts = new Dictionary<string, EmptyStruct> { [$"{enginePort}/tcp"] = default },
                HostConfig = new HostConfig
                {
                    NetworkMode = IngressNetwork,
                    PortBindings = new Dictionary<string, IList<PortBinding>>
                    {
                        [$"{enginePort}/tcp"] = new List<PortBinding> { new() { HostPort = port.ToString() } }
                    },
                    Memory = 64L * 1024 * 1024,
                    NanoCPUs = 250_000_000L,           // 0.25 CPU
                    PidsLimit = 50L,
                    CapDrop = new List<string> { "ALL" },
                    SecurityOpt = new List<string> { "no-new-privileges" },
                    RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No }
                }
            }, ct);
            sidecarId = sidecarCreate.ID;

            // Connect sidecar to mayfly-users so socat can reach the DB by container name.
            await _docker.Networks.ConnectNetworkAsync(UserNetwork, new NetworkConnectParameters
            {
                Container = sidecarId
            }, ct);

            await _docker.Containers.StartContainerAsync(sidecarId, new ContainerStartParameters(), ct);

            // Wait for the engine to accept connections. With init-scripts, Postgres runs role
            // setup and optional seeding before it opens its port. We poll via Docker exec
            // (no network access required) so that the API only returns 201 after the DB is
            // genuinely ready to serve queries.
            await WaitForEngineReadyAsync(containerId, provider.ReadinessExec(creds), ct);

            // For engines that skip the init-volume path, run PostReadyExec via docker-exec.
            // We capture stdout+stderr so that a non-zero exit code produces a meaningful error
            // message — a silent failure here would leave a broken instance with no app user.
            if (!provider.UsesInitVolume && setup.PostReadyExec is not null)
            {
                const int postReadyMaxBytes = 64 * 1024;
                var (exitCode, stdout, stderr, _) = await ExecCaptureAsync(
                    containerId, new List<string>(setup.PostReadyExec), env: null, postReadyMaxBytes, ct);
                if (exitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"PostReadyExec exited with code {exitCode}. " +
                        $"stdout: {stdout.Trim()} | stderr: {stderr.Trim()}");
                }
            }

            return new CreateInstanceResult(containerId, volume!, name, port, creds.Db,
                DbUser: creds.AppUser, DbPassword: creds.AppPassword,
                AdminUser: creds.AdminUser, AdminPassword: creds.AdminPassword);
        }
        catch
        {
            if (sidecarId is not null)
            {
                try { await _docker.Containers.RemoveContainerAsync(sidecarId, new ContainerRemoveParameters { Force = true }, ct); }
                catch (Exception ex) { _log.LogWarning(ex, "cleanup: remove sidecar {Id} failed", sidecarId); }
            }
            if (containerId is not null)
            {
                try { await _docker.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true }, ct); }
                catch (Exception ex) { _log.LogWarning(ex, "cleanup: remove container {Id} failed", containerId); }
            }
            if (writerId is not null)
            {
                try { await _docker.Containers.RemoveContainerAsync(writerId, new ContainerRemoveParameters { Force = true }, ct); }
                catch (Exception ex) { _log.LogWarning(ex, "cleanup: remove writer {Id} failed", writerId); }
            }
            if (initVolumeName is not null)
            {
                try { await _docker.Volumes.RemoveAsync(initVolumeName, force: true, ct); }
                catch (Exception ex) { _log.LogWarning(ex, "cleanup: destroy init volume {Volume} failed", initVolumeName); }
            }
            if (volume is not null)
            {
                try { await _volumes.DestroyAsync(volume, ct); }
                catch (Exception ex) { _log.LogWarning(ex, "cleanup: destroy volume {Volume} failed", volume); }
            }
            _ports.Release(port);
            throw;
        }
    }

    public async Task DestroyAsync(string containerId, string volumeName, int publicPort, CancellationToken ct)
    {
        // Derive the init-volume name from the data-volume name without a container inspect.
        // This is safe even when the container is already gone (reconcile Direction-1).
        string? initVolumeName = volumeName.StartsWith("mayfly-vol-")
            ? volumeName.Replace("mayfly-vol-", "mayfly-init-")
            : null;

        // Derive instanceId from volumeName to locate the sidecar by label.
        string? instanceId = volumeName.StartsWith("mayfly-vol-")
            ? volumeName["mayfly-vol-".Length..]
            : null;

        string? sidecarId = null;
        if (instanceId is not null)
        {
            try
            {
                var sidecars = await _docker.Containers.ListContainersAsync(new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["label"] = new Dictionary<string, bool>
                        {
                            [$"mayfly.instance={instanceId}"] = true,
                            ["mayfly.role=sidecar"] = true
                        }
                    }
                }, ct);
                sidecarId = sidecars.FirstOrDefault()?.ID;
            }
            catch (Exception ex) { _log.LogWarning(ex, "destroy: locate sidecar for {Id} failed", instanceId); }
        }

        try { await _docker.Containers.RemoveContainerAsync(containerId,
            new ContainerRemoveParameters { Force = true }, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "destroy: remove container {Id} failed", containerId); }

        if (sidecarId is not null)
        {
            try { await _docker.Containers.RemoveContainerAsync(sidecarId,
                new ContainerRemoveParameters { Force = true }, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "destroy: remove sidecar {Id} failed", sidecarId); }
        }

        try { await _volumes.DestroyAsync(volumeName, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "destroy: destroy volume {Volume} failed", volumeName); }

        if (initVolumeName is not null)
        {
            try { await _docker.Volumes.RemoveAsync(initVolumeName, force: true, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "destroy: destroy init volume {Volume} failed", initVolumeName); }
        }

        _ports.Release(publicPort);
    }

    public async Task<InspectResult> InspectAsync(string containerId, CancellationToken ct)
    {
        var c = await _docker.Containers.InspectContainerAsync(containerId,
            new ContainerInspectParameters { IncludeSize = true }, ct);
        long size = c.SizeRootFs ?? c.SizeRw ?? 0;
        var state = c.State?.Running == true ? "running" : (c.State?.Status ?? "unknown");
        return new InspectResult(state, size);
    }

    public async Task<IReadOnlyList<ManagedContainerInfo>> ListManagedAsync(CancellationToken ct)
    {
        var containers = await _docker.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = false,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { ["mayfly.role=db"] = true }
            }
        }, ct);

        return containers
            .Where(c => c.Labels.ContainsKey("mayfly.instance"))
            .Select(c => new ManagedContainerInfo(c.ID, c.Labels["mayfly.instance"]))
            .ToList();
    }

    public async Task DestroyByInstanceAsync(string instanceId, CancellationToken ct)
    {
        // Queries by mayfly.instance label — catches both the DB and the sidecar container.
        var containers = await _docker.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { [$"mayfly.instance={instanceId}"] = true }
            }
        }, ct);

        foreach (var c in containers)
        {
            string? volumeName = c.Mounts?.FirstOrDefault(m => m.Type == "volume")?.Name;
            // The sidecar holds the host port binding; the DB no longer publishes any port.
            int port = 0;
            var portEntry = c.Ports?.FirstOrDefault(p => p.PublicPort > 0);
            if (portEntry?.PublicPort is ushort pp) port = pp;

            try
            {
                await _docker.Containers.RemoveContainerAsync(c.ID,
                    new ContainerRemoveParameters { Force = true }, ct);
            }
            catch (Exception ex) { _log.LogWarning(ex, "DestroyByInstance: remove {Id} failed", c.ID); }

            if (!string.IsNullOrEmpty(volumeName))
            {
                try { await _volumes.DestroyAsync(volumeName, ct); }
                catch (Exception ex) { _log.LogWarning(ex, "DestroyByInstance: remove volume {Vol} failed", volumeName); }
            }

            if (port > 0) _ports.Release(port);
        }

        // Remove ALL volumes labelled mayfly.instance=<id> (covers data + init + any leaked volumes).
        // Listing with a label filter is more robust than by-convention naming: it catches every
        // volume written during a crashed provisioning run, including the credential-bearing init volume.
        try
        {
            var labelledVolumes = await _docker.Volumes.ListAsync(new VolumesListParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool>
                    {
                        [$"mayfly.instance={instanceId}"] = true
                    }
                }
            }, ct);

            foreach (var vol in labelledVolumes.Volumes ?? [])
            {
                try { await _docker.Volumes.RemoveAsync(vol.Name, force: true, ct); }
                catch (Exception ex) { _log.LogWarning(ex, "DestroyByInstance: remove labelled volume {Vol} failed", vol.Name); }
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "DestroyByInstance: list labelled volumes for {Id} failed", instanceId); }
    }

    /// <summary>
    /// Removes all orphan writer containers (labelled <c>mayfly.role=writer</c>) and any
    /// <c>mayfly-vol-*</c>, <c>mayfly-init-*</c>, or <c>mayfly.instance</c>-labelled volumes
    /// whose name is not in <paramref name="activeVolumeNames"/>.
    /// Writer containers are always transient; any survivor of a crashed provision run is an orphan.
    /// </summary>
    public async Task SweepOrphansAsync(IReadOnlyCollection<string> activeVolumeNames, CancellationToken ct)
    {
        // Remove all writer containers — they are transient and never survive a healthy provision.
        try
        {
            var writers = await _docker.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool> { ["mayfly.role=writer"] = true }
                }
            }, ct);

            foreach (var c in writers)
            {
                try { await _docker.Containers.RemoveContainerAsync(c.ID, new ContainerRemoveParameters { Force = true }, ct); }
                catch (Exception ex) { _log.LogWarning(ex, "sweep: remove orphan writer {Id} failed", c.ID); }
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "sweep: list orphan writers failed"); }

        // Remove orphan mayfly volumes not in the active set.
        try
        {
            var allVolumes = await _docker.Volumes.ListAsync(new VolumesListParameters(), ct);
            foreach (var vol in allVolumes.Volumes ?? [])
            {
                bool isMayfly = vol.Name.StartsWith("mayfly-vol-") ||
                                vol.Name.StartsWith("mayfly-init-") ||
                                (vol.Labels?.ContainsKey("mayfly.instance") == true);

                if (isMayfly && !activeVolumeNames.Contains(vol.Name))
                {
                    try { await _docker.Volumes.RemoveAsync(vol.Name, force: true, ct); }
                    catch (Exception ex) { _log.LogWarning(ex, "sweep: remove orphan volume {Vol} failed", vol.Name); }
                }
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "sweep: list volumes for orphan sweep failed"); }
    }

    /// <summary>
    /// Builds an in-memory tar stream containing the engine init scripts to be uploaded
    /// into the engine's initdb directory via the init-scripts volume.
    /// </summary>
    private static MemoryStream BuildInitScriptsTar(IReadOnlyList<(string FileName, string Sql)> scripts)
    {
        var ms = new MemoryStream();
        using (var tw = new TarWriter(ms, TarEntryFormat.Gnu, leaveOpen: true))
        {
            foreach (var (fileName, sql) in scripts)
            {
                WriteTarEntry(tw, fileName, sql);
            }
        } // TarWriter.Dispose() writes the end-of-archive marker

        ms.Position = 0;
        return ms;
    }

    private static void WriteTarEntry(TarWriter tw, string name, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var entry = new GnuTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(bytes),
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                   UnixFileMode.GroupRead | UnixFileMode.OtherRead
        };
        tw.WriteEntry(entry);
    }

    // -------------------------------------------------------------------------
    // ExecMongoshAsync
    // -------------------------------------------------------------------------

    public async Task<ExecMongoshResult> ExecMongoshAsync(
        string containerId, ExecMongoshRequest req, CancellationToken ct)
    {
        // Pass the user's JS (MONGO_CMD) and the password (MONGO_PWD) via the exec's ENV so
        // neither is visible on the shell command line or in docker inspect. The double-quoted
        // $MONGO_CMD / $MONGO_PWD expansions inside `sh -c` are not parsed by the shell as
        // command text, so there is no shell-injection surface even when the user's JS contains
        // special characters.
        var sh = $"timeout {req.TimeoutSeconds} mongosh --quiet --host 127.0.0.1 -u {req.User} " +
                 $"-p \"$MONGO_PWD\" --authenticationDatabase {req.AuthDb} appdb --eval \"$MONGO_CMD\"";
        var cmd = new List<string> { "sh", "-c", sh };
        var env = new List<string> { $"MONGO_PWD={req.Password}", $"MONGO_CMD={req.Command}" };

        // Outer safety CTS: cancels after TimeoutSeconds + 5 s so the API call can never hang
        // even if the container-side `timeout` misbehaves.
        using var outerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        outerCts.CancelAfter(TimeSpan.FromSeconds(req.TimeoutSeconds + 5));

        var sw = Stopwatch.StartNew();
        var (exit, outStr, errStr, truncated) =
            await ExecCaptureAsync(containerId, cmd, env, req.MaxOutputBytes, outerCts.Token);
        return new ExecMongoshResult(outStr, errStr, exit, truncated, (int)sw.ElapsedMilliseconds);
    }

    // -------------------------------------------------------------------------
    // Shared exec helper
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates and runs a docker exec, capturing stdout and stderr into strings capped at
    /// <paramref name="maxBytes"/> total (combined). A <see cref="CappedStream"/> discards bytes
    /// beyond the cap during streaming so that a runaway <c>print</c> loop cannot OOM the
    /// Provisioner process. Returns the exit code, captured strings, and a truncation flag.
    /// </summary>
    private async Task<(int exit, string stdout, string stderr, bool truncated)> ExecCaptureAsync(
        string containerId, IList<string> cmd, IList<string>? env, int maxBytes, CancellationToken ct)
    {
        var execParams = new ContainerExecCreateParameters
        {
            Cmd = new List<string>(cmd),
            AttachStdout = true,
            AttachStderr = true
        };
        if (env is not null) execParams.Env = new List<string>(env);
        var execCreate = await _docker.Exec.CreateContainerExecAsync(containerId, execParams, ct);

        using var execStream = await _docker.Exec.StartContainerExecAsync(
            execCreate.ID,
            new ContainerExecStartParameters { Detach = false },
            ct);

        // Each of stdout and stderr gets its own cap; the total readable bytes is at most
        // 2 × maxBytes, which is acceptable for the console use-case. An alternative would be
        // a single shared cap, but per-stream caps keep the implementation simpler.
        using var stdoutCapped = new CappedStream(maxBytes);
        using var stderrCapped = new CappedStream(maxBytes);
        await execStream.CopyOutputToAsync(Stream.Null, stdoutCapped, stderrCapped, ct);

        var execInspect = await _docker.Exec.InspectContainerExecAsync(execCreate.ID, ct);

        var stdout = Encoding.UTF8.GetString(stdoutCapped.Buffer, 0, stdoutCapped.BytesWritten);
        var stderr = Encoding.UTF8.GetString(stderrCapped.Buffer, 0, stderrCapped.BytesWritten);
        bool truncated = stdoutCapped.Truncated || stderrCapped.Truncated;

        return ((int)(execInspect.ExitCode ?? 0), stdout, stderr, truncated);
    }

    // -------------------------------------------------------------------------
    // CappedStream: accepts up to maxBytes, discards the rest, sets Truncated flag
    // -------------------------------------------------------------------------

    /// <summary>
    /// A write-only stream that accepts up to <see cref="MaxBytes"/> bytes and then
    /// silently discards further writes while setting <see cref="Truncated"/> to true.
    /// Used as the output target for docker exec streams to bound memory usage even if
    /// the container produces arbitrarily large output.
    /// </summary>
    private sealed class CappedStream : Stream
    {
        private readonly int _maxBytes;
        private int _written;
        private bool _truncated;

        public CappedStream(int maxBytes)
        {
            _maxBytes = maxBytes;
            Buffer = new byte[maxBytes];
        }

        public byte[] Buffer { get; }
        public int BytesWritten => _written;
        public bool Truncated => _truncated;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            int remaining = _maxBytes - _written;
            if (remaining <= 0)
            {
                _truncated = true;
                return;
            }
            int toCopy = Math.Min(count, remaining);
            System.Buffer.BlockCopy(buffer, offset, Buffer, _written, toCopy);
            _written += toCopy;
            if (toCopy < count) _truncated = true;
        }

        public override void WriteByte(byte value)
        {
            if (_written >= _maxBytes) { _truncated = true; return; }
            Buffer[_written++] = value;
        }
    }

    private async Task EnsureImageAsync(string image, CancellationToken ct)
    {
        var existing = await _docker.Images.ListImagesAsync(new ImagesListParameters { All = true }, ct);
        if (existing.Any(i => i.RepoTags?.Contains(image) == true)) return;
        var colon = image.IndexOf(':');
        var fromImage = colon >= 0 ? image[..colon] : image;
        var tag = colon >= 0 ? image[(colon + 1)..] : "latest";
        await _docker.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = fromImage, Tag = tag },
            null, new Progress<JSONMessage>(), ct);
    }

    private async Task EnsureSidecarImageAsync(CancellationToken ct)
    {
        var existing = await _docker.Images.ListImagesAsync(new ImagesListParameters { All = true }, ct);
        if (existing.Any(i => i.RepoTags?.Contains(SidecarImage) == true)) return;
        await _docker.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = "alpine/socat", Tag = "1.8.0.0" },
            null, new Progress<JSONMessage>(), ct);
    }

    /// <summary>
    /// Polls the engine's readiness command inside the DB container via Docker exec until
    /// the command exits with code 0 or the timeout elapses.
    /// </summary>
    private async Task WaitForEngineReadyAsync(string containerId, IList<string> readinessCmd, CancellationToken ct)
    {
        const int maxAttempts = 75;   // 75 × 2 s = 150 s ceiling (Northwind seeding ~60 s)
        for (int i = 0; i < maxAttempts; i++)
        {
            try
            {
                var execCreate = await _docker.Exec.CreateContainerExecAsync(
                    containerId,
                    new ContainerExecCreateParameters
                    {
                        Cmd = new List<string>(readinessCmd),
                        AttachStdout = true,
                        AttachStderr = true
                    },
                    ct);

                using var execStream = await _docker.Exec.StartContainerExecAsync(
                    execCreate.ID,
                    new ContainerExecStartParameters { Detach = false },
                    ct);
                // Drain the stream so the exec completes
                await execStream.CopyOutputToAsync(Stream.Null, Stream.Null, Stream.Null, ct);

                var inspect = await _docker.Exec.InspectContainerExecAsync(execCreate.ID, ct);
                if (inspect.ExitCode == 0)
                {
                    _log.LogInformation("Engine ready in container {Id} after {Attempt} poll(s)", containerId, i + 1);
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "Readiness poll {Attempt} failed; retrying", i + 1);
            }
            await Task.Delay(2000, ct);
        }
        throw new TimeoutException($"Engine in container {containerId} did not accept connections within 150 s");
    }

    private async Task EnsureNetworksAsync(CancellationToken ct)
    {
        var nets = await _docker.Networks.ListNetworksAsync(cancellationToken: ct);

        if (!nets.Any(n => n.Name == UserNetwork))
        {
            try
            {
                await _docker.Networks.CreateNetworkAsync(new NetworksCreateParameters
                {
                    Name = UserNetwork,
                    Driver = "bridge",
                    Internal = true  // No gateway → no internet egress
                }, ct);
            }
            catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict) { }
        }

        if (!nets.Any(n => n.Name == IngressNetwork))
        {
            try
            {
                await _docker.Networks.CreateNetworkAsync(new NetworksCreateParameters
                {
                    Name = IngressNetwork,
                    Driver = "bridge",
                    Internal = false
                }, ct);
            }
            catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict) { }
        }
    }
}
