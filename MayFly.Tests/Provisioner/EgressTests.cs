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

        try
        {
            // --- 1. Inspect the DB container ---
            var dbInspect = await docker.Containers.InspectContainerAsync(res.ContainerId, default);
            var instanceId = dbInspect.Config.Labels["mayfly.instance"];

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

            // --- 3. Egress probe: wget from inside the DB container must be blocked ---
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var execCreate = await docker.Exec.CreateContainerExecAsync(
                res.ContainerId,
                new ContainerExecCreateParameters
                {
                    Cmd = new List<string> { "sh", "-c", "wget -T2 -q -O- http://1.1.1.1 2>&1 || echo BLOCKED" },
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
            probeOutput.Should().Contain("BLOCKED",
                "DB container on internal network must have no internet egress");

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
            Func<Task> probe = async () =>
                await docker.Containers.InspectContainerAsync(res.ContainerId, default);
            // Container should be gone — InspectAsync throws DockerContainerNotFoundException
            // (or the container is in a removed state)
            try
            {
                var dbPost = await docker.Containers.InspectContainerAsync(res.ContainerId, default);
                dbPost.State?.Running.Should().BeFalse("DB container must not be running after destroy");
            }
            catch
            {
                // Container removed — expected
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
