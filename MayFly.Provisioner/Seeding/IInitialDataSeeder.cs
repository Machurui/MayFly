namespace MayFly.Provisioner.Seeding;

public interface IInitialDataSeeder
{
    Task SeedAsync(string initialData, string host, int port, string db, string user, string password, CancellationToken ct);
}
