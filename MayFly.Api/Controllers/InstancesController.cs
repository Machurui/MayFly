using MayFly.Api.Data;
using MayFly.Api.Domain;
using MayFly.Api.Dtos;
using MayFly.Api.Engines;
using MayFly.Api.Import;
using MayFly.Api.Mongo;
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
    MayFlyContext db, EngineClientRegistry registry, IMongoOps mongoOps, IDumpImporter dumpImporter)
    : ControllerBase
{
    private string PublicHost => cfg["PublicHost"] ?? "localhost";
    private string Sid => HttpContext.Items[SessionCookieMiddleware.CookieName] as string ?? "";
    private string Ip => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";

    private string DisplayCs(Instance i)
    {
        if (i.Engine == "mongo")
        {
            var pwd = secrets.Unprotect(i.DbPasswordEnc);
            return $"mongodb://{i.DbUser}:{pwd}@{PublicHost}:{i.PublicPort}/{i.DbName}";
        }
        return registry.For(i.Engine).BuildDisplayConnectionString(
            PublicHost, i.PublicPort, i.DbName, i.DbUser, secrets.Unprotect(i.DbPasswordEnc));
    }

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
            InstanceDto.From(inst, DisplayCs(inst)));
    }

    [HttpGet("{token}")]
    public async Task<IActionResult> GetByToken(string token, CancellationToken ct)
    {
        var inst = await instances.GetByTokenAsync(token, ct);
        return inst is null ? NotFound()
            : Ok(InstanceDto.From(inst, DisplayCs(inst)));
    }

    [HttpGet]
    public async Task<IActionResult> ListMine(CancellationToken ct)
    {
        var list = await instances.ListBySessionAsync(Sid, ct);
        return Ok(list.Select(i => InstanceDto.From(i, DisplayCs(i))));
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
        var result = inst.Engine == "mongo"
            ? await mongoOps.RunConsoleAsync(inst, body.Query, ct)
            : await queryExec.ExecuteAsync(inst, body.Query, ct);
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

    [HttpPost("{token}/import")]
    [EnableRateLimiting("import")]
    [RequestSizeLimit(16 * 1024 * 1024)]
    public async Task<IActionResult> Import(string token, IFormFile file, CancellationToken ct)
    {
        var inst = await instances.GetByTokenAsync(token, ct);
        if (inst is null) return NotFound();
        if (file is null || file.Length == 0) return BadRequest("no file");
        if (file.Length > 16L * 1024 * 1024) return StatusCode(StatusCodes.Status413PayloadTooLarge);
        using var reader = new StreamReader(file.OpenReadStream());
        var content = await reader.ReadToEndAsync(ct);
        var result = await dumpImporter.ImportAsync(inst, content, ct);
        return Ok(result);
    }
}
