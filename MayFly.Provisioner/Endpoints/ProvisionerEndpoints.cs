using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Docker;
using MayFly.Provisioner.Seeding;
using MayFly.Provisioner.Validation;

namespace MayFly.Provisioner.Endpoints;

public static class ProvisionerEndpoints
{
    public static void MapProvisioner(this WebApplication app)
    {
        app.MapPost("/instances", async (CreateInstanceRequest req, IDockerProvisioner p,
            IInitialDataSeeder seeder, IConfiguration cfg, CancellationToken ct) =>
        {
            var (ok, error) = InstanceSpecValidator.Validate(req);
            if (!ok) return Results.BadRequest(new { error });
            var result = await p.CreateAsync(req, ct);
            var useInternal = cfg.GetValue("Provisioner:UseInternalHost", true);
            var host = useInternal ? result.InternalHost : "localhost";
            var port = useInternal ? 5432 : result.PublicPort;
            await seeder.SeedAsync(req.InitialData, host, port, result.DbName, result.DbUser, result.DbPassword, ct);
            return Results.Ok(result);
        });

        app.MapDelete("/instances/{containerId}", async (string containerId, string volume, int port,
            IDockerProvisioner p, CancellationToken ct) =>
        {
            await p.DestroyAsync(containerId, volume, port, ct);
            return Results.NoContent();
        });

        app.MapGet("/instances/{containerId}", async (string containerId, IDockerProvisioner p, CancellationToken ct) =>
        {
            try { return Results.Ok(await p.InspectAsync(containerId, ct)); }
            catch (global::Docker.DotNet.DockerContainerNotFoundException) { return Results.NotFound(); }
        });
    }
}
