using FluentAssertions;
using MayFly.Api.Security;
using Xunit;

public class TokenServiceTests
{
    private readonly TokenService _sut = new();

    [Fact]
    public void NewToken_is_long_and_urlsafe()
    {
        var t = _sut.NewToken();
        t.Length.Should().BeGreaterThanOrEqualTo(43);   // 32 bytes base64url
        t.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
    }

    [Fact]
    public void NewToken_is_unique() => _sut.NewToken().Should().NotBe(_sut.NewToken());

    [Fact]
    public void Matches_true_for_equal_false_otherwise()
    {
        var t = _sut.NewToken();
        _sut.Matches(t, t).Should().BeTrue();
        _sut.Matches(t, _sut.NewToken()).Should().BeFalse();
        _sut.Matches("short", t).Should().BeFalse();
    }
}
