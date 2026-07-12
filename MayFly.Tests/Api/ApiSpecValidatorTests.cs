using FluentAssertions;
using MayFly.Api.Dtos;
using MayFly.Api.Validation;
using Xunit;

public class ApiSpecValidatorTests
{
    // ── Valid combinations ────────────────────────────────────────────────────
    [Theory]
    [InlineData("postgres",  3,  256, "blank")]
    [InlineData("postgres", 12, 2048, "northwind")]
    [InlineData("mysql",     3,  256, "blank")]
    [InlineData("mysql",     3,  256, "northwind")]
    [InlineData("mariadb",   6,  512, "blank")]
    [InlineData("mariadb",   6,  512, "northwind")]
    [InlineData("mssql",    12, 1024, "blank")]
    [InlineData("mssql",    12, 1024, "northwind")]
    public void Validate_accepts_supported_engines_with_blank(string e, int ttl, int mb, string init)
        => ApiSpecValidator.Validate(new CreateInstanceDto(e, ttl, mb, init)).Ok.Should().BeTrue();

    // ── Unknown engine rejected ───────────────────────────────────────────────
    [Theory]
    [InlineData("oracle")]
    [InlineData("sqlserver")]   // old mis-named id must NOT be accepted
    [InlineData("mongodb")]
    [InlineData("")]
    public void Validate_rejects_unknown_engine(string e)
        => ApiSpecValidator.Validate(new CreateInstanceDto(e, 3, 256, "blank")).Ok.Should().BeFalse();

    // ── Northwind accepted on all SQL engines (cross-engine restriction removed in SP3) ──
    [Theory]
    [InlineData("mysql")]
    [InlineData("mariadb")]
    [InlineData("mssql")]
    public void Validate_accepts_northwind_for_all_sql_engines(string e)
        => ApiSpecValidator.Validate(new CreateInstanceDto(e, 3, 256, "northwind")).Ok.Should().BeTrue();

    [Fact]
    public void Validate_accepts_northwind_for_postgres()
        => ApiSpecValidator.Validate(new CreateInstanceDto("postgres", 3, 256, "northwind")).Ok.Should().BeTrue();

    // ── Engine error reported before initialData error ────────────────────────
    [Fact]
    public void Validate_reports_engine_error_before_initialData_error()
    {
        var (ok, err) = ApiSpecValidator.Validate(new CreateInstanceDto("oracle", 3, 256, "northwind"));
        ok.Should().BeFalse();
        err.Should().Be("engine not supported");
    }
}
