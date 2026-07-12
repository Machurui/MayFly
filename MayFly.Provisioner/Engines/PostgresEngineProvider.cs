using System.Security.Cryptography;
using Docker.DotNet.Models;
using MayFly.Provisioner.Seeding;

namespace MayFly.Provisioner.Engines;

public sealed class PostgresEngineProvider : IEngineProvider
{
    public string EngineId => "postgres";
    public string Image => "postgres:16-alpine";
    public int Port => 5432;
    public bool UsesInitVolume => true;
    public string DataDirectory => "/var/lib/postgresql/data";

    public EngineCredentials GenerateCredentials()
    {
        var adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        return new EngineCredentials("mayflyadmin", adminPassword, "appuser", appPassword, "appdb");
    }

    public IList<string> BuildEnv(EngineCredentials c) =>
        new List<string>
        {
            $"POSTGRES_USER={c.AdminUser}",
            $"POSTGRES_PASSWORD={c.AdminPassword}",
            $"POSTGRES_DB={c.Db}"
        };

    public EngineSetup BuildSetup(EngineCredentials c, string initialData)
    {
        var scripts = new List<(string FileName, string Sql)>
        {
            ("00-roles.sql", BuildRolesSql(c))
        };

        if (SeedCatalog.IsTemplate(initialData))
        {
            var seedSql = SeedCatalog.GetSql(initialData) +
                $"\nGRANT ALL ON ALL TABLES IN SCHEMA public TO {c.AppUser};" +
                $"\nGRANT ALL ON ALL SEQUENCES IN SCHEMA public TO {c.AppUser};\n";
            scripts.Add(("01-seed.sql", seedSql));
        }

        return new EngineSetup(scripts, PostReadyExec: null);
    }

    public IList<string> ReadinessExec(EngineCredentials c) =>
        new List<string> { "pg_isready", "-h", "127.0.0.1", "-U", c.AdminUser, "-q" };

    public void ApplyHardening(global::Docker.DotNet.Models.HostConfig hc)
    {
        hc.ReadonlyRootfs = true;
        hc.Tmpfs = new Dictionary<string, string>
        {
            ["/tmp"] = "rw,noexec,nosuid,size=64m",
            ["/var/run/postgresql"] = "rw,noexec,nosuid,size=16m",
            ["/run"] = "rw,noexec,nosuid,size=16m"
        };
        hc.Memory = 256L * 1024 * 1024;
        hc.NanoCPUs = 500_000_000L;
        hc.PidsLimit = 200L;
        hc.CapDrop = new List<string> { "ALL" };
        hc.CapAdd = new List<string> { "CHOWN", "SETUID", "SETGID", "FOWNER", "DAC_OVERRIDE" };
        hc.SecurityOpt = new List<string> { "no-new-privileges" };
    }

    private static string BuildRolesSql(EngineCredentials c)
    {
        var pwdLiteral = c.AppPassword.Replace("'", "''");
        return
            $"CREATE ROLE \"{c.AppUser}\" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '{pwdLiteral}';\n" +
            $"GRANT CONNECT ON DATABASE \"{c.Db}\" TO \"{c.AppUser}\";\n" +
            $"GRANT ALL ON SCHEMA public TO \"{c.AppUser}\";\n" +
            $"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO \"{c.AppUser}\";\n" +
            $"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON SEQUENCES TO \"{c.AppUser}\";\n" +
            "CREATE EXTENSION IF NOT EXISTS pg_trgm;\n" +
            "CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\";\n";
    }


}
