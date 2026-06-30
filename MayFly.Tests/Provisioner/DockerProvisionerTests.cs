using Docker.DotNet;
using FluentAssertions;
using MayFly.Provisioner.Docker;
using Xunit;

[Trait("Category", "Docker")]
public class VolumeProvisionerTests
{
    [Fact]
    public async Task Plain_create_and_destroy_roundtrips()
    {
        var sut = new PlainVolumeProvisioner(
            new DockerClientBuilder().Build());
        var name = await sut.CreateAsync(Guid.NewGuid().ToString("N"), 256, default);
        name.Should().StartWith("mayfly-vol-");
        await sut.DestroyAsync(name, default);   // must not throw
    }
}
