using Docker.DotNet;
using MayFly.Provisioner.Docker;
using MayFly.Provisioner.Endpoints;

namespace MayFly.Provisioner;

public interface IProvisionerMarker { }   // marker for WebApplicationFactory

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddSingleton<IDockerClient>(_ => new DockerClientBuilder().Build());
        builder.Services.AddSingleton<IPortAllocator>(_ => new PortAllocator(Array.Empty<int>()));

        var useXfs = builder.Configuration.GetValue("Provisioner:UseXfsQuota", false);
        builder.Services.AddSingleton<IVolumeProvisioner>(sp =>
        {
            var d = sp.GetRequiredService<IDockerClient>();
            return useXfs ? new XfsVolumeProvisioner(d) : (IVolumeProvisioner)new PlainVolumeProvisioner(d);
        });

        builder.Services.AddSingleton<IDockerProvisioner, DockerProvisioner>();

        var app = builder.Build();

        var key = builder.Configuration["Provisioner:Key"];
        app.Use(async (ctx, next) =>
        {
            if (!string.IsNullOrWhiteSpace(key) &&
                ctx.Request.Headers["X-Provisioner-Key"] != key)
            { ctx.Response.StatusCode = 401; return; }
            await next();
        });

        app.MapProvisioner();
        app.Run();
    }
}
