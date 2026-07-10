using FluentAssertions;
using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Validation;
using Xunit;

public class InstanceSpecValidatorTests
{
    [Theory]
    [InlineData("postgres",  3,    256, "blank")]
    [InlineData("postgres",  12,  2048, "northwind")]
    [InlineData("mysql",     3,    256, "blank")]
    [InlineData("mariadb",   3,    256, "blank")]
    [InlineData("mssql",     3,   1024, "blank")]
    public void Validate_accepts_allowed_values(string e, int ttl, int mb, string init)
        => InstanceSpecValidator.Validate(new CreateInstanceRequest(e, ttl, mb, init)).Ok.Should().BeTrue();

    [Theory]
    [InlineData("oracle", 3, 256, "blank")]     // engine not in allowed set
    [InlineData("postgres", 5, 256, "blank")]   // ttl not allowed
    [InlineData("postgres", 3, 999, "blank")]   // storage not allowed
    [InlineData("postgres", 3, 256, "ecom")]    // initialData not allowed
    public void Validate_rejects_out_of_whitelist(string e, int ttl, int mb, string init)
        => InstanceSpecValidator.Validate(new CreateInstanceRequest(e, ttl, mb, init)).Ok.Should().BeFalse();
}
