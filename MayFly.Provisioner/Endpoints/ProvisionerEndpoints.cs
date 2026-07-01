using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Docker;
using MayFly.Provisioner.Validation;

namespace MayFly.Provisioner.Endpoints;

public static class ProvisionerEndpoints
{
    public static void MapProvisioner(this WebApplication app)
    {
        app.MapPost("/instances", async (CreateInstanceRequest req, IDockerProvisioner p, CancellationToken ct) =>
        {
            var (ok, error) = InstanceSpecValidator.Validate(req);
            if (!ok) return Results.BadRequest(new { error });
            var result = await p.CreateAsync(req, ct);
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
            catch { return Results.NotFound(); }
        });
    }
}
