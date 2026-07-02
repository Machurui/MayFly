using MayFly.Api.Data;
using MayFly.Api.Lifecycle;
using MayFly.Api.Provisioning;
using MayFly.Api.Security;
using MayFly.Api.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<MayFlyContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Metadata")));

builder.Services.AddHttpClient<IProvisionerClient, ProvisionerClient>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Provisioner:BaseUrl"] ?? "http://provisioner:8080"));

builder.Services.AddDataProtection();
builder.Services.AddSingleton<ISecretProtector, SecretProtector>();
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddScoped<IInstanceService, InstanceService>();
builder.Services.AddScoped<IQueryExecutor, QueryExecutor>();
builder.Services.AddHostedService<LifecycleService>();

builder.Services.AddControllers();

builder.Services.AddRateLimiter(o => o.AddFixedWindowLimiter("perip", w =>
{
    w.Window = TimeSpan.FromMinutes(1);
    w.PermitLimit = 60;
}));

builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

var app = builder.Build();

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
