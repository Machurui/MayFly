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
        _factory = f;
        _client = f.CreateClient();
    }

    [Fact]
    public async Task Create_rejects_disallowed_engine_with_400()
    {
        var resp = await _client.PostAsJsonAsync("/instances",
            new CreateInstanceRequest("mysql", 3, 256, "blank"));
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
        var resp = await client.GetAsync("/instances/whatever");
        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }
}
