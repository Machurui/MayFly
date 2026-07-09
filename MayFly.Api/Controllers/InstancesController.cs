using MayFly.Api.Data;
using MayFly.Api.Domain;
using MayFly.Api.Dtos;
using MayFly.Api.Security;
using MayFly.Api.Services;
using MayFly.Api.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MayFly.Api.Controllers;

[ApiController]
[Route("api/instances")]
public sealed class InstancesController(
    IInstanceService instances, IQueryExecutor queryExec, ISecretProtector secrets, IConfiguration cfg,
    MayFlyContext db)
    : ControllerBase
{
    private string PublicHost => cfg["PublicHost"] ?? "localhost";
    private string Sid => HttpContext.Items[SessionCookieMiddleware.CookieName] as string ?? "";
    private string Ip => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";

    [HttpPost]
    [EnableRateLimiting("create")]
    public async Task<IActionResult> Create([FromBody] CreateInstanceDto dto, CancellationToken ct)
    {
        var (ok, error) = ApiSpecValidator.Validate(dto);
        if (!ok) return BadRequest(new { error });
        var outcome = await instances.CreateAsync(dto.Engine, dto.TtlHours, dto.StorageMb,
            dto.InitialData, Ip, Sid, ct);
        if (outcome.QuotaExceeded) return StatusCode(429, new { error = "IP quota of 3 active databases reached" });
        var inst = outcome.Instance!;
        return CreatedAtAction(nameof(GetByToken), new { token = inst.CapabilityToken },
            InstanceDto.From(inst, PublicHost, secrets.Unprotect(inst.DbPasswordEnc)));
    }

    [HttpGet("{token}")]
    public async Task<IActionResult> GetByToken(string token, CancellationToken ct)
    {
        var inst = await instances.GetByTokenAsync(token, ct);
        return inst is null ? NotFound()
            : Ok(InstanceDto.From(inst, PublicHost, secrets.Unprotect(inst.DbPasswordEnc)));
    }

    [HttpGet]
    public async Task<IActionResult> ListMine(CancellationToken ct)
    {
        var list = await instances.ListBySessionAsync(Sid, ct);
        return Ok(list.Select(i => InstanceDto.From(i, PublicHost, secrets.Unprotect(i.DbPasswordEnc))));
    }

    [HttpDelete("{token}")]
    public async Task<IActionResult> Destroy(string token, CancellationToken ct)
        => await instances.DestroyAsync(token, ct) ? NoContent() : NotFound();

    [HttpPost("{token}/query")]
    [EnableRateLimiting("query")]
    public async Task<IActionResult> Query(string token, [FromBody] QueryRequestDto body, CancellationToken ct)
    {
        var inst = await instances.GetByTokenAsync(token, ct);
        if (inst is null) return NotFound();
        var result = await queryExec.ExecuteAsync(inst, body.Sql, ct);
        try
        {
            db.QueryLogs.Add(new QueryLog
            {
                InstanceId   = inst.Id,
                ExecutedAt   = DateTime.UtcNow,
                DurationMs   = result.DurationMs,
                RowCount     = result.RowCount,
                Success      = result.Success,
                ErrorMessage = result.Error,
            });
            await db.SaveChangesAsync(ct);
        }
        catch { /* best-effort: query-log persistence must not break the query response */ }
        return Ok(result);
    }
}
