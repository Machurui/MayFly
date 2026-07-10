using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Docker;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Xunit;

public class ProvisionerEndpointsTests : IClassFixture<WebApplicationFactory<MayFly.Provisioner.IProvisionerMarker>>
{
    private readonly WebApplicationFactory<MayFly.Provisioner.IProvisionerMarker> _factory;
    private readonly HttpClient _client;

    public ProvisionerEndpointsTests(WebApplicationFactory<MayFly.Provisioner.IProvisionerMarker> f)
    {
        _factory = f.WithWebHostBuilder(b => b.UseSetting("Provisioner:Key", "test-key"));
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Provisioner-Key", "test-key");
    }

    [Fact]
    public async Task Request_without_key_is_401()
    {
        var client = _factory.CreateClient();  // no X-Provisioner-Key header
        var resp = await client.PostAsJsonAsync("/instances",
            new CreateInstanceRequest("postgres", 3, 256, "blank"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_rejects_disallowed_engine_with_400()
    {
        var resp = await _client.PostAsJsonAsync("/instances",
            new CreateInstanceRequest("oracle", 3, 256, "blank"));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Inspect_surfaces_non_notfound_error_as_500()
    {
        var mock = new Mock<IDockerProvisioner>();
        mock.Setup(p => p.InspectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var client = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll(typeof(IDockerProvisioner));
                s.AddSingleton(mock.Object);
            })).CreateClient();
        client.DefaultRequestHeaders.Add("X-Provisioner-Key", "test-key");
        var resp = await client.GetAsync("/instances/whatever");
        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task SweepOrphans_returns_204_and_delegates_to_provisioner()
    {
        var mock = new Mock<IDockerProvisioner>();
        mock.Setup(p => p.SweepOrphansAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var client = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll(typeof(IDockerProvisioner));
                s.AddSingleton(mock.Object);
            })).CreateClient();
        client.DefaultRequestHeaders.Add("X-Provisioner-Key", "test-key");

        var resp = await client.PostAsJsonAsync("/sweep-orphans",
            new MayFly.Provisioner.Contracts.SweepOrphansRequest(new[] { "mayfly-vol-active1" }));
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        mock.Verify(p => p.SweepOrphansAsync(
            It.Is<IReadOnlyCollection<string>>(v => v.Contains("mayfly-vol-active1")),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
