using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MayFly.Provisioner.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

public class ProvisionerEndpointsTests : IClassFixture<WebApplicationFactory<MayFly.Provisioner.IProvisionerMarker>>
{
    private readonly HttpClient _client;
    public ProvisionerEndpointsTests(WebApplicationFactory<MayFly.Provisioner.IProvisionerMarker> f)
        => _client = f.CreateClient();

    [Fact]
    public async Task Create_rejects_disallowed_engine_with_400()
    {
        var resp = await _client.PostAsJsonAsync("/instances",
            new CreateInstanceRequest("mysql", 3, 256, "blank"));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
