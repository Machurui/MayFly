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
            Content = JsonContent.Create(new ProvisionResult("cid","vol","host",20001,"appdb","appuser","pw"))
        };
        var http = new HttpClient(new StubHandler(resp)) { BaseAddress = new Uri("http://provisioner") };
        var sut = new ProvisionerClient(http);
        var r = await sut.CreateAsync("postgres", 3, 256, "blank", default);
        r.PublicPort.Should().Be(20001);
        r.DbName.Should().Be("appdb");
    }
}
