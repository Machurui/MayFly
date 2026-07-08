using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Docker;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

[Trait("Category", "Docker")]
public class VolumeProvisionerTests
{
    [Fact]
    public async Task Plain_create_and_destroy_roundtrips()
    {
        var sut = new PlainVolumeProvisioner(
            new DockerClientBuilder().Build());
        var name = await sut.CreateAsync(Guid.NewGuid().ToString("N"), 256, default);
        name.Should().StartWith("mayfly-vol-");
        await sut.DestroyAsync(name, default);   // must not throw
    }
}

[Collection("docker-sequential")]
[Trait("Category", "Docker")]
public class DockerProvisionerLifecycleTests
{
    private static IDockerProvisioner NewSut()
    {
        var docker = new DockerClientBuilder().Build();
        return new DockerProvisioner(docker,
            new PortAllocator(Array.Empty<int>()),
            new PlainVolumeProvisioner(docker),
            NullLogger<DockerProvisioner>.Instance);
    }

    [Fact]
    public async Task Create_yields_reachable_postgres_then_destroy_cleans_up()
    {
        var sut = NewSut();
        var dockerClient = new DockerClientBuilder().Build();
        var res = await sut.CreateAsync(new CreateInstanceRequest("postgres", 3, 256, "blank"), default);
        try
        {
            res.PublicPort.Should().BeInRange(20000, 21000);
            // connect via the published public port (localhost in dev)
            var cs = $"Host=localhost;Port={res.PublicPort};Database={res.DbName};" +
                     $"Username={res.DbUser};Password={res.DbPassword}";
            await WaitForPostgresAsync(cs);
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            (await cmd.ExecuteScalarAsync()).Should().Be(1);

            var inspect = await sut.InspectAsync(res.ContainerId, default);
            inspect.State.Should().Be("running");
            inspect.SizeBytes.Should().BeGreaterThan(0);

            // Hardening assertions: verify the container was actually started with the
            // required security constraints and resource limits.
            var containerInspect = await dockerClient.Containers.InspectContainerAsync(res.ContainerId, default);
            var hc = containerInspect.HostConfig;
            hc.Should().NotBeNull();
            hc!.CapDrop.Should().Contain("ALL");
            hc.CapAdd.Should().BeEquivalentTo(
                new[] { "CHOWN", "SETUID", "SETGID", "FOWNER", "DAC_OVERRIDE" });
            hc.Memory.Should().Be(256L * 1024 * 1024);
            hc.NanoCPUs.Should().Be(500_000_000L);
            hc.PidsLimit.Should().Be(200L);
            hc.SecurityOpt.Should().Contain("no-new-privileges");
            hc.Mounts.Should().Contain(m => m.Type == "volume");

            // Read-only rootfs: DB container must run with a read-only root filesystem.
            hc.ReadonlyRootfs.Should().BeTrue("DB container must use read-only rootfs");

            // Tmpfs: required writable paths must be mounted as tmpfs.
            hc.Tmpfs.Should().NotBeNull("DB container must have tmpfs mounts for runtime-writable paths");
            hc.Tmpfs.Should().ContainKey("/tmp", "postgres needs a writable /tmp");
            hc.Tmpfs.Should().ContainKey("/var/run/postgresql", "postgres socket directory must be writable");
            hc.Tmpfs.Should().ContainKey("/run", "/run must be writable for runtime pid/lock files");

            // Verify write to /etc fails (read-only rootfs enforcement).
            var execCreate = await dockerClient.Exec.CreateContainerExecAsync(
                res.ContainerId,
                new ContainerExecCreateParameters
                {
                    Cmd = new List<string> { "sh", "-c", "echo x > /etc/x && echo WRITABLE || echo READONLY" },
                    AttachStdout = true,
                    AttachStderr = true
                },
                default);

            using var execStream = await dockerClient.Exec.StartContainerExecAsync(
                execCreate.ID,
                new ContainerExecStartParameters { Detach = false },
                default);

            using var stdoutBuf = new MemoryStream();
            using var stderrBuf = new MemoryStream();
            await execStream.CopyOutputToAsync(Stream.Null, stdoutBuf, stderrBuf, default);

            var writeOutput = Encoding.UTF8.GetString(stdoutBuf.ToArray())
                            + Encoding.UTF8.GetString(stderrBuf.ToArray());
            writeOutput.Should().Contain("READONLY",
                "writes to /etc must be rejected by read-only rootfs");
            writeOutput.Should().NotContain("WRITABLE",
                "read-only rootfs must prevent writes to /etc; WRITABLE is only printed when the write succeeds");
        }
        finally
        {
            await sut.DestroyAsync(res.ContainerId, res.VolumeName, res.PublicPort, default);
        }

        // Assert the credential-bearing init volume is removed after destroy.
        var initVolumeName = res.VolumeName.Replace("mayfly-vol-", "mayfly-init-");
        var allVolumes = await dockerClient.Volumes.ListAsync(new VolumesListParameters(), default);
        allVolumes.Volumes.Should().NotContain(v => v.Name == initVolumeName,
            "DestroyAsync must remove the credential-bearing init volume to prevent at-rest leaks");
    }

    private static async Task WaitForPostgresAsync(string cs)
    {
        for (int i = 0; i < 30; i++)
        {
            try { await using var c = new NpgsqlConnection(cs); await c.OpenAsync(); return; }
            catch { await Task.Delay(1000); }
        }
        throw new TimeoutException("postgres did not become ready");
    }
}
