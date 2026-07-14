using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Docker;
using MayFly.Provisioner.Engines;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

[Trait("Category", "Docker")]
[Collection("docker-sequential")]
public class SeedTemplateMongoTests
{
    private static IDockerProvisioner NewSut()
    {
        var docker = new DockerClientBuilder().Build();
        return new DockerProvisioner(
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
    }

    [Theory(Timeout = 120_000)]
    [InlineData("northwind", "products")]
    [InlineData("ecommerce", "products")]
    [InlineData("blog",      "posts")]
    [InlineData("iot",       "sensor_readings")]
    public async Task Template_seeds_mongo_collection_readable_by_appuser(string template, string collection)
    {
        await CleanLeakedContainersAsync();

        var sut = NewSut();
        var res = await sut.CreateAsync(
            new CreateInstanceRequest("mongo", 3, 256, template), default);

        try
        {
            var connectionString =
                $"mongodb://{res.DbUser}:{res.DbPassword}@localhost:{res.PublicPort}/{res.DbName}" +
                $"?authSource={res.DbName}";

            await WaitForMongoAsync(connectionString, res.DbName);

            var client = new MongoClient(connectionString);
            var db = client.GetDatabase(res.DbName);

            var count = await db
                .GetCollection<BsonDocument>(collection)
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);

            count.Should().BeGreaterThan(0,
                $"seed '{template}' must have populated the '{collection}' collection");
        }
        finally
        {
            await sut.DestroyAsync(res.ContainerId, res.VolumeName, res.PublicPort, default);
        }
    }

    private static async Task WaitForMongoAsync(string connectionString, string dbName)
    {
        Exception? lastEx = null;
        for (int i = 0; i < 90; i++)
        {
            try
            {
                var client = new MongoClient(
                    MongoClientSettings.FromConnectionString(connectionString));
                var db = client.GetDatabase(dbName);
                await db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
                return;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                await Task.Delay(1000);
            }
        }
        throw new TimeoutException(
            $"MongoDB not ready after 90 attempts: {lastEx?.Message}", lastEx);
    }

    private static async Task CleanLeakedContainersAsync()
    {
        try
        {
            using var docker = new DockerClientBuilder().Build();
            var containers = await docker.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["name"] = new Dictionary<string, bool> { ["mayfly-"] = true }
                    }
                }, default);

            foreach (var c in containers)
            {
                try
                {
                    await docker.Containers.RemoveContainerAsync(
                        c.ID, new ContainerRemoveParameters { Force = true }, default);
                }
                catch { /* best-effort */ }
            }
        }
        catch { /* best-effort */ }
    }
}
