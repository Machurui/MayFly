using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MayFly.Api.Provisioning;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

// Custom factory that supplies the metadata connection string so EF startup does not throw.
// The "bad engine" test hits the validator before any DB access, so no live DB is required.
public sealed class MayFlyWebFactory : WebApplicationFactory<MayFly.Api.IApiMarker>
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        // "Testing" env skips the startup Migrate() (no live metadata DB in this test).
        builder.UseSetting("environment", "Testing");
        builder.UseSetting("ConnectionStrings:Metadata",
            "Host=localhost;Port=5433;Database=mayfly;Username=mayfly;Password=mayfly");
    }
}

public class InstancesApiTests : IClassFixture<MayFlyWebFactory>
{
    private readonly MayFlyWebFactory _factory;
    public InstancesApiTests(MayFlyWebFactory f) => _factory = f;

    [Fact]
    public async Task Create_with_bad_engine_returns_400()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/instances",
            new { engine = "oracle", ttlHours = 3, storageMb = 256, initialData = "blank" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
