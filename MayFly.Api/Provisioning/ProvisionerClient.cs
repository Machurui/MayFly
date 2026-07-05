using System.Net.Http.Json;

namespace MayFly.Api.Provisioning;

public sealed class ProvisionerClient(HttpClient http) : IProvisionerClient
{
    public async Task<ProvisionResult> CreateAsync(string engine, int ttl, int storageMb, string initData, CancellationToken ct)
    {
        var resp = await http.PostAsJsonAsync("/instances",
            new CreateBody(engine, ttl, storageMb, initData), ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ProvisionResult>(cancellationToken: ct))!;
    }

    public async Task DestroyAsync(string containerId, string volume, int port, CancellationToken ct)
    {
        var resp = await http.DeleteAsync($"/instances/{containerId}?volume={volume}&port={port}", ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<ProvisionInspect> InspectAsync(string containerId, CancellationToken ct)
    {
        var resp = await http.GetAsync($"/instances/{containerId}", ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ProvisionInspect>(cancellationToken: ct))!;
    }

    public async Task<IReadOnlyList<ManagedContainer>> ListManagedAsync(CancellationToken ct)
    {
        var resp = await http.GetAsync("/managed", ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<List<ManagedContainer>>(cancellationToken: ct))!;
    }

    public async Task DestroyByInstanceAsync(string instanceId, CancellationToken ct)
    {
        var resp = await http.DeleteAsync($"/managed/{instanceId}", ct);
        resp.EnsureSuccessStatusCode();
    }
}
