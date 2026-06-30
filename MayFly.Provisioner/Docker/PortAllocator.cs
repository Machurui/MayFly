namespace MayFly.Provisioner.Docker;

public sealed class PortAllocator : IPortAllocator
{
    private const int Min = 20000, Max = 21000;
    private readonly HashSet<int> _used;
    private readonly object _lock = new();

    public PortAllocator(IEnumerable<int> inUse) => _used = new HashSet<int>(inUse);

    public int Allocate()
    {
        lock (_lock)
        {
            for (int p = Min; p <= Max; p++)
                if (_used.Add(p)) return p;
            throw new InvalidOperationException("port range exhausted");
        }
    }

    public void Release(int port) { lock (_lock) _used.Remove(port); }
}
