using FluentAssertions;
using MayFly.Api.Security;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

public class SecretProtectorTests
{
    [Fact]
    public void Roundtrip_returns_original_and_ciphertext_differs()
    {
        var sut = new SecretProtector(DataProtectionProvider.Create("MayFlyTest"));
        var enc = sut.Protect("s3cr3t");
        enc.Should().NotBe("s3cr3t");
        sut.Unprotect(enc).Should().Be("s3cr3t");
    }
}
