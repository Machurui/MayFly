using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Docker;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using System.Text;
using Xunit;

[Trait("Category", "Docker")]
[Collection("docker-sequential")]
public class EgressTests
{
    private static (IDockerClient docker, IDockerProvisioner sut) NewSut()
    {
        var docker = new DockerClientBuilder().Build();
        var sut = new DockerProvisioner(docker,
            new PortAllocator(Array.Empty<int>()),
            new PlainVolumeProvisioner(docker),
            NullLogger<DockerProvisioner>.Instance);
        return (docker, sut);
    }

    [Fact]
    public async Task User_db_has_no_internet_but_is_reachable_via_sidecar()
    {
        var (docker, sut) = NewSut();
        var res = await sut.CreateAsync(new CreateInstanceRequest("postgres", 3, 256, "blank"), default);
        string? instanceId = null;

        try
        {
            // --- 0. Direct config guard: mayfly-users must be an internal network ---
            // Primary regression guard: if Internal=false is set on the network (e.g. by a
            // provisioner bug), this assertion catches it before any egress probe runs.
            var net = await docker.Networks.InspectNetworkAsync("mayfly-users");
            net.Internal.Should().BeTrue(
                "mayfly-users must be created with Internal=true; a regression to false would open internet egress for all user DB containers");

            // --- 1. Inspect the DB container ---
            var dbInspect = await docker.Containers.InspectContainerAsync(res.ContainerId, default);
            instanceId = dbInspect.Config.Labels["mayfly.instance"];

            // DB must be on mayfly-users (internal) only — no PortBindings
            dbInspect.NetworkSettings.Networks.Should().ContainKey("mayfly-users",
                "DB container must be on the internal user network");
            dbInspect.NetworkSettings.Networks.Should().NotContainKey("mayfly-ingress",
                "DB container must NOT be on the ingress network");

            var dbPortBindings = dbInspect.HostConfig?.PortBindings;
            bool dbHasNoPublishedPort = dbPortBindings == null
                || dbPortBindings.Count == 0
                || dbPortBindings.All(kv => kv.Value == null || kv.Value.Count == 0);
            dbHasNoPublishedPort.Should().BeTrue("DB container must not publish any ports");

            // --- 2. Verify sidecar exists and is dual-homed ---
            var sidecars = await docker.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = false,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool>
                    {
                        [$"mayfly.instance={instanceId}"] = true,
                        ["mayfly.role=sidecar"] = true
                    }
                }
            }, default);
            sidecars.Should().HaveCount(1, "exactly one sidecar per instance");

            var sidecarInspect = await docker.Containers.InspectContainerAsync(sidecars[0].ID, default);
            sidecarInspect.NetworkSettings.Networks.Should().ContainKey("mayfly-users",
                "sidecar must be connected to internal user network to reach DB");
            sidecarInspect.NetworkSettings.Networks.Should().ContainKey("mayfly-ingress",
                "sidecar must be on ingress network to publish the port");

            var sidecarPortBindings = sidecarInspect.HostConfig?.PortBindings;
            bool sidecarPublishesPort = sidecarPortBindings != null
                && sidecarPortBindings.Any(kv => kv.Value?.Count > 0);
            sidecarPublishesPort.Should().BeTrue("sidecar must publish the public port");

            // --- 3. Egress probe: TCP dial from inside the DB container must fail ---
            // Probe: try to establish a raw TCP connection to 1.1.1.1:80 using busybox nc
            // (available in postgres:16-alpine via busybox). With Internal=true on mayfly-users,
            // there is no default gateway so nc times out and exits non-zero → NOEGRESS is printed.
            // If egress were open, nc would connect, get EOF on stdin, exit 0 → REACHED is printed.
            // REACHED is a positive success marker: its ABSENCE unambiguously proves the block.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var execCreate = await docker.Exec.CreateContainerExecAsync(
                res.ContainerId,
                new ContainerExecCreateParameters
                {
                    Cmd = new List<string> { "sh", "-c", "(nc -w2 1.1.1.1 80 && echo REACHED) || echo NOEGRESS" },
                    AttachStdout = true,
                    AttachStderr = true
                },
                cts.Token);

            using var execStream = await docker.Exec.StartContainerExecAsync(
                execCreate.ID,
                new ContainerExecStartParameters { Detach = false },
                cts.Token);

            using var stdoutBuf = new MemoryStream();
            using var stderrBuf = new MemoryStream();
            await execStream.CopyOutputToAsync(Stream.Null, stdoutBuf, stderrBuf, cts.Token);

            var probeOutput = Encoding.UTF8.GetString(stdoutBuf.ToArray())
                            + Encoding.UTF8.GetString(stderrBuf.ToArray());
            probeOutput.Should().Contain("NOEGRESS",
                "DB container on internal network must have no internet egress; NOEGRESS is printed only when nc fails to connect");
            probeOutput.Should().NotContain("REACHED",
                "REACHED is only printed when nc succeeds — its presence means egress is open, a security regression");

            // --- 4. Connect via the sidecar's published host port ---
            var cs = $"Host=localhost;Port={res.PublicPort};Database={res.DbName};" +
                     $"Username={res.DbUser};Password={res.DbPassword}";
            await WaitForPostgresAsync(cs);
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            (await cmd.ExecuteScalarAsync()).Should().Be(1,
                "sidecar must proxy connections to the DB");
            await conn.CloseAsync();
        }
        finally
        {
            await sut.DestroyAsync(res.ContainerId, res.VolumeName, res.PublicPort, default);

            // Verify DB container is gone
            await Task.Delay(500);
            try
            {
                var dbPost = await docker.Containers.InspectContainerAsync(res.ContainerId, default);
                dbPost.State?.Running.Should().BeFalse("DB container must not be running after destroy");
            }
            catch
            {
                // Container removed — expected
            }

            // Fix 2: Verify sidecar was also removed by DestroyAsync (guards against sidecar leaks)
            if (instanceId is not null)
            {
                var sidecarsPost = await docker.Containers.ListContainersAsync(new ContainersListParameters
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
                }, default);
                sidecarsPost.Should().BeEmpty(
                    "DestroyAsync must remove the sidecar; a leak would leave a container consuming resources and holding the port");
            }
        }
    }

    private static async Task WaitForPostgresAsync(string cs)
    {
        for (int i = 0; i < 30; i++)
        {
            try
            {
                await using var c = new NpgsqlConnection(cs);
                await c.OpenAsync();
                return;
            }
            catch { await Task.Delay(1000); }
        }
        throw new TimeoutException("postgres did not become ready via sidecar within 30 s");
    }
}
