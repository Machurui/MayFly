namespace MayFly.Provisioner.Docker;
public interface IPortAllocator { int Allocate(); void Release(int port); }
