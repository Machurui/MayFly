using Docker.DotNet;
using FluentAssertions;
using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Docker;
using MayFly.Provisioner.Engines;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

[Trait("Category", "Docker")]
[Collection("docker-sequential")]
public class ExecMongoshTests
{
    private static (IDockerClient docker, IDockerProvisioner sut) NewSut()
    {
        var docker = new DockerClientBuilder().Build();
        var sut = new DockerProvisioner(
            docker,
            new PortAllocator(Array.Empty<int>()),
            new PlainVolumeProvisioner(docker),
            new IEngineProvider[]
            {
                new PostgresEngineProvider(),
                new MySqlEngineProvider(),
                new MariaDbEngineProvider(),
                new SqlServerEngineProvider(),
                new MongoEngineProvider()
            },
            NullLogger<DockerProvisioner>.Instance);
        return (docker, sut);
    }

    [Fact(Timeout = 120_000)]
    public async Task Exec_runs_mongosh_captures_output_and_enforces_timeout()
    {
        var (_, sut) = NewSut();
        var res = await sut.CreateAsync(new CreateInstanceRequest("mongo", 3, 256, "blank"), default);

        try
        {
            // --- 1. Normal command: insert a doc and count ---
            var insertReq = new ExecMongoshRequest(
                Command: "db.getCollection('t').insertOne({x:1}); print(db.getCollection('t').countDocuments())",
                User: res.DbUser,
                Password: res.DbPassword,
                AuthDb: res.DbName,
                TimeoutSeconds: 10,
                MaxOutputBytes: 64 * 1024);

            var insertResult = await sut.ExecMongoshAsync(res.ContainerId, insertReq, default);

            insertResult.ExitCode.Should().Be(0, "insert + count must succeed as appuser");
            insertResult.Output.Should().Contain("1", "countDocuments() should return 1 after one insert");
            insertResult.Ms.Should().BeGreaterThan(0);
            insertResult.Truncated.Should().BeFalse("output is small, should not be truncated");

            // --- 2. Bad / syntax-error command ---
            var badReq = new ExecMongoshRequest(
                Command: "THIS IS NOT VALID JS |||",
                User: res.DbUser,
                Password: res.DbPassword,
                AuthDb: res.DbName,
                TimeoutSeconds: 10,
                MaxOutputBytes: 64 * 1024);

            var badResult = await sut.ExecMongoshAsync(res.ContainerId, badReq, default);

            badResult.ExitCode.Should().NotBe(0, "a syntax error should produce a non-zero exit code");
            badResult.Error.Should().NotBeNullOrEmpty("mongosh should write error information to stderr");

            // --- 3. Runaway command: timeout must kill it ---
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var runawayReq = new ExecMongoshRequest(
                Command: "while(true){}",
                User: res.DbUser,
                Password: res.DbPassword,
                AuthDb: res.DbName,
                TimeoutSeconds: 3,
                MaxOutputBytes: 64 * 1024);

            var runawayResult = await sut.ExecMongoshAsync(res.ContainerId, runawayReq, default);
            sw.Stop();

            runawayResult.ExitCode.Should().NotBe(0, "timeout must kill the runaway loop with non-zero exit");
            sw.Elapsed.TotalSeconds.Should().BeLessThan(15,
                "runaway must be killed within ~8s (3s inner timeout + 5s outer buffer); not hang the test");

            // --- 4. Output cap: huge print must be truncated ---
            // Print a ~200KB string with a 1KB cap
            var bigPrintReq = new ExecMongoshRequest(
                Command: "print('A'.repeat(200000))",
                User: res.DbUser,
                Password: res.DbPassword,
                AuthDb: res.DbName,
                TimeoutSeconds: 10,
                MaxOutputBytes: 1024);

            var bigResult = await sut.ExecMongoshAsync(res.ContainerId, bigPrintReq, default);

            bigResult.ExitCode.Should().Be(0, "the command itself should succeed");
            bigResult.Truncated.Should().BeTrue("200KB output with a 1KB cap must be flagged as truncated");
            bigResult.Output.Length.Should().BeLessThanOrEqualTo(1024,
                "output must be capped at MaxOutputBytes");
        }
        finally
        {
            await sut.DestroyAsync(res.ContainerId, res.VolumeName, res.PublicPort, default);
        }
    }
}
