namespace MayFly.Api.Security;

public sealed class SessionCookieMiddleware(RequestDelegate next)
{
    public const string CookieName = "mayfly_sid";

    public async Task Invoke(HttpContext ctx)
    {
        if (!ctx.Request.Cookies.TryGetValue(CookieName, out var sid) || string.IsNullOrWhiteSpace(sid))
        {
            sid = Guid.NewGuid().ToString("N");
            ctx.Response.Cookies.Append(CookieName, sid, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = true,
                Expires = DateTimeOffset.UtcNow.AddDays(30), IsEssential = true
            });
        }
        ctx.Items[CookieName] = sid;
        await next(ctx);
    }
}
