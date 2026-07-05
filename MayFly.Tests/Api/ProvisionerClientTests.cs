using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MayFly.Api.Provisioning;
using Xunit;

public class ProvisionerClientTests
{
    private sealed class StubHandler(HttpResponseMessage resp) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c)
            => Task.FromResult(resp);
    }

    [Fact]
    public async Task CreateAsync_maps_result()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ProvisionResult("cid","vol","host",20001,"appdb","appuser","pw","mayflyadmin","adminpw"))
        };
        var http = new HttpClient(new StubHandler(resp)) { BaseAddress = new Uri("http://provisioner") };
        var sut = new ProvisionerClient(http);
        var r = await sut.CreateAsync("postgres", 3, 256, "blank", default);
        r.PublicPort.Should().Be(20001);
        r.DbName.Should().Be("appdb");
    }

    [Fact]
    public async Task InspectAsync_throws_on_non_success_status()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var http = new HttpClient(new StubHandler(resp)) { BaseAddress = new Uri("http://provisioner") };
        var sut = new ProvisionerClient(http);
        await Assert.ThrowsAsync<HttpRequestException>(() => sut.InspectAsync("cid", default));
    }
}
