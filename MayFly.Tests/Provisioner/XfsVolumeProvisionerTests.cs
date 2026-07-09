using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using MayFly.Provisioner.Docker;
using Moq;
using Xunit;

[Trait("Category", "Unit")]
public class XfsVolumeProvisionerTests
{
    [Fact]
    public async Task CreateAsync_builds_local_volume_with_size_only_no_type_or_o()
    {
        // Arrange
        var dockerClientMock = new Mock<IDockerClient>();
        var volumeOperationsMock = new Mock<IVolumeOperations>();

        VolumesCreateParameters? capturedParams = null;
        volumeOperationsMock
            .Setup(v => v.CreateAsync(It.IsAny<VolumesCreateParameters>(), It.IsAny<CancellationToken>()))
            .Callback<VolumesCreateParameters, CancellationToken>((p, _) => capturedParams = p)
            .ReturnsAsync(new VolumeResponse { Name = "mayfly-vol-test-instance-123" });

        dockerClientMock
            .Setup(d => d.Volumes)
            .Returns(volumeOperationsMock.Object);

        var sut = new XfsVolumeProvisioner(dockerClientMock.Object);
        var instanceId = "test-instance-123";
        var storageMb = 256;

        // Act
        var volumeName = await sut.CreateAsync(instanceId, storageMb, default);

        // Assert
        volumeName.Should().Be($"mayfly-vol-{instanceId}");

        capturedParams.Should().NotBeNull();
        capturedParams!.Driver.Should().Be("local");

        // Assert DriverOpts contains ONLY "size" - no "type" or "o"
        capturedParams.DriverOpts.Should().NotBeNull();
        capturedParams.DriverOpts!.Keys.Should().BeEquivalentTo(new[] { "size" });
        capturedParams.DriverOpts["size"].Should().Be("256m");

        // Assert label is present
        capturedParams.Labels.Should().NotBeNull();
        capturedParams.Labels!.Should().ContainKey("mayfly.instance")
            .WhoseValue.Should().Be(instanceId);

        // Verify the method was called once with the correct parameters
        volumeOperationsMock.Verify(
            v => v.CreateAsync(It.IsAny<VolumesCreateParameters>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
