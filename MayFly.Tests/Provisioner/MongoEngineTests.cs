using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Docker;
using MayFly.Provisioner.Engines;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using System.Text;
using Xunit;

[Trait("Category", "Docker")]
[Collection("docker-sequential")]
public class MongoEngineTests
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
    public async Task Mongo_appuser_scoped_readWrite_only_and_reachable()
    {
        var (docker, sut) = NewSut();
        var res = await sut.CreateAsync(new CreateInstanceRequest("mongo", 3, 256, "blank"), default);

        try
        {
            // --- Basic credential shape ---
            res.DbUser.Should().Be("appuser");
            res.AdminUser.Should().Be("mayflyadmin");

            // --- Connect as appuser via sidecar published port ---
            var connectionString =
                $"mongodb://{res.DbUser}:{res.DbPassword}@localhost:{res.PublicPort}/{res.DbName}?authSource={res.DbName}";

            await WaitForMongoAsync(connectionString);

            var client = new MongoClient(connectionString);
            var db = client.GetDatabase(res.DbName);

            // --- 1. Insert + Find succeeds (readWrite on appdb) ---
            var collection = db.GetCollection<MongoDB.Bson.BsonDocument>("test_col");
            var doc = new MongoDB.Bson.BsonDocument { { "probe", "mayfly-test" }, { "ts", MongoDB.Bson.BsonDateTime.Create(DateTime.UtcNow) } };
            await collection.InsertOneAsync(doc);

            var found = await collection
                .Find(new MongoDB.Bson.BsonDocument("probe", "mayfly-test"))
                .FirstOrDefaultAsync();
            found.Should().NotBeNull("appuser must be able to insert and find in appdb");

            // --- 2. Admin operation is REJECTED (not privileged) ---
            // appuser has readWrite only on appdb. Writing to any other database must
            // return code 13 (Unauthorized). We insert into "other" — a database that
            // appuser has no access to at all.
            var otherDb = client.GetDatabase("other");
            var adminOpEx = await Assert.ThrowsAnyAsync<MongoException>(
                () => otherDb.GetCollection<MongoDB.Bson.BsonDocument>("t")
                             .InsertOneAsync(new MongoDB.Bson.BsonDocument("probe", 1)));

            // MongoDB "Unauthorized" is error code 13. Verify the rejection is actually an
            // authorization denial, not a connection/timeout failure.
            if (adminOpEx is MongoDB.Driver.MongoWriteException we)
            {
                we.WriteError?.Code.Should().Be(13, "unauthorized write must be code 13, not a connection/timeout failure");
            }
            else if (adminOpEx is MongoDB.Driver.MongoCommandException ce)
            {
                ce.Code.Should().Be(13, "unauthorized write must be code 13, not a connection/timeout failure");
            }
            else
            {
                adminOpEx.Message.Should().MatchRegex("(?i)not authorized|unauthorized",
                    "the rejection must be an authorization denial, not a connection/timeout failure");
            }

            // --- 3. Egress probe from DB container must fail ---
            // mongo:7 is Debian — has bash and /dev/tcp
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var execCreate = await docker.Exec.CreateContainerExecAsync(
                res.ContainerId,
                new ContainerExecCreateParameters
                {
                    Cmd = new List<string>
                    {
                        "bash", "-c",
                        "(timeout 3 bash -c 'echo >/dev/tcp/1.1.1.1/80' 2>/dev/null && echo REACHED) || echo NOEGRESS"
                    },
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
                "DB container on internal network must have no internet egress");
            probeOutput.Should().NotContain("REACHED",
                "REACHED is only printed when egress is open — a security regression");
        }
        finally
        {
            await sut.DestroyAsync(res.ContainerId, res.VolumeName, res.PublicPort, default);
        }
    }

    private static async Task WaitForMongoAsync(string connectionString)
    {
        Exception? lastEx = null;
        for (int i = 0; i < 90; i++)
        {
            try
            {
                var client = new MongoClient(MongoClientSettings.FromConnectionString(connectionString));
                var db = client.GetDatabase("appdb");
                await db.RunCommandAsync<MongoDB.Bson.BsonDocument>(
                    new MongoDB.Bson.BsonDocument("ping", 1));
                return;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                await Task.Delay(1000);
            }
        }
        throw new TimeoutException($"MongoDB not ready after 90 attempts: {lastEx?.Message}", lastEx);
    }
}
