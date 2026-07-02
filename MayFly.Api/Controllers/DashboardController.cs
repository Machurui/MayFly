using MayFly.Api.Security;
using MayFly.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace MayFly.Api.Controllers;

[ApiController]
[Route("api/dashboard")]
public sealed class DashboardController(DashboardService dashboard) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var sid = HttpContext.Items[SessionCookieMiddleware.CookieName] as string ?? "";
        return Ok(await dashboard.SummaryAsync(sid, ct));
    }
}
