using MayFly.Api.Data;
using MayFly.Api.Lifecycle;
using MayFly.Api.Provisioning;
using MayFly.Api.Security;
using MayFly.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<MayFlyContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Metadata")));

builder.Services.AddHttpClient<IProvisionerClient, ProvisionerClient>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Provisioner:BaseUrl"] ?? "http://provisioner:8080");
    c.DefaultRequestHeaders.Add("X-Provisioner-Key", builder.Configuration["Provisioner:Key"] ?? "");
    // Northwind seeding via init-scripts can take ~60 s; allow 180 s for the full create flow.
    c.Timeout = TimeSpan.FromSeconds(180);
});

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo("/keys"))
    .SetApplicationName("MayFly");
builder.Services.AddSingleton<ISecretProtector, SecretProtector>();
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddScoped<IInstanceService, InstanceService>();
builder.Services.AddScoped<IQueryExecutor, QueryExecutor>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<QuotaEnforcer>();
builder.Services.AddHostedService<LifecycleService>();

builder.Services.AddControllers();

builder.Services.AddRateLimiter(o =>
{
    o.AddPolicy("perip", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1)
            }));
    o.AddPolicy("create", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 6,
                Window = TimeSpan.FromMinutes(1)
            }));
    o.AddPolicy("query", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1)
            }));
});

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Trust RFC1918 ranges so X-Forwarded-For from Caddy is honoured for per-IP quota.
    // Safe because the API port is never published to the host — only Caddy reaches it
    // over the private compose network.
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
    o.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.Parse("10.0.0.0"), 8));
    o.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.Parse("172.16.0.0"), 12));
    o.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.Parse("192.168.0.0"), 16));
});

var app = builder.Build();

// Apply schema at startup for real deployments. Skipped under the "Testing" environment,
// where WebApplicationFactory boots the app without a live metadata DB.
if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MayFlyContext>();
    db.Database.Migrate();
}

app.UseForwardedHeaders();
app.UseMiddleware<SessionCookieMiddleware>();
app.UseRateLimiter();
app.MapControllers().RequireRateLimiting("perip");

app.Run();

namespace MayFly.Api
{
    public interface IApiMarker { }
}

public partial class Program { }
