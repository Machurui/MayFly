namespace MayFly.Api.Engines;

public sealed class EngineClientRegistry
{
    private readonly Dictionary<string, IEngineClient> _clients;

    public EngineClientRegistry(IEnumerable<IEngineClient> clients)
        => _clients = clients.ToDictionary(c => c.EngineId, c => c);

    public IEngineClient For(string engineId)
        => _clients.TryGetValue(engineId, out var c) ? c
            : throw new InvalidOperationException($"No engine client registered for engine '{engineId}'.");
}
