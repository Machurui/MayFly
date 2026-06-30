using System.Collections.Concurrent;
using FluentAssertions;
using MayFly.Provisioner.Docker;
using Xunit;

public class PortAllocatorTests
{
    [Fact]
    public void Allocate_returns_port_in_range()
    {
        var p = new PortAllocator(Array.Empty<int>()).Allocate();
        p.Should().BeInRange(20000, 21000);
    }

    [Fact]
    public void Allocate_skips_ports_in_use()
        => new PortAllocator(new[] { 20000 }).Allocate().Should().NotBe(20000);

    [Fact]
    public void Released_port_can_be_reallocated()
    {
        var a = new PortAllocator(Array.Empty<int>());
        var taken = new HashSet<int>();
        for (int i = 0; i <= 1000; i++) taken.Add(a.Allocate());
        Action exhausted = () => a.Allocate();
        exhausted.Should().Throw<InvalidOperationException>();
        a.Release(20500);
        a.Allocate().Should().Be(20500);
    }

    [Fact]
    public void Allocate_is_unique_under_concurrency()
    {
        var a = new PortAllocator(Array.Empty<int>());
        var bag = new ConcurrentBag<int>();
        Parallel.For(0, 500, _ => bag.Add(a.Allocate()));
        bag.Distinct().Count().Should().Be(500);
    }
}
