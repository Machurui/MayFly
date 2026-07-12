using System.Security.Cryptography;
using Docker.DotNet.Models;
using MayFly.Provisioner.Seeding;

namespace MayFly.Provisioner.Engines;

public sealed class SqlServerEngineProvider : IEngineProvider
{
    public string EngineId => "mssql";
    public string Image => "mcr.microsoft.com/mssql/server:2022-latest";
    public int Port => 1433;
    public bool UsesInitVolume => false;   // no initdb.d -> docker-exec setup after readiness
    public string DataDirectory => "/var/opt/mssql";

    public EngineCredentials GenerateCredentials()
    {
        // SQL Server SA password policy: >=8 chars, >=3 of 4 categories
        // (upper, lower, digit, symbol). Hex provides lower+digit; suffix adds upper+symbol.
        static string Strong() =>
            Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant() + "Aa1!";
        return new EngineCredentials("sa", Strong(), "appuser", Strong(), "appdb");
    }

    public IList<string> BuildEnv(EngineCredentials c) =>
        new List<string> { "ACCEPT_EULA=Y", $"MSSQL_SA_PASSWORD={c.AdminPassword}" };

    public EngineSetup BuildSetup(EngineCredentials c, string initialData)
    {
        // CREATE DATABASE must be in its own batch; USE must precede database-scoped DDL.
        // sqlcmd -Q processes GO as a batch separator even in non-interactive mode, so we
        // embed GO between the three logical batches in one sqlcmd invocation.
        // Docker exec passes argv directly (no shell), so no shell escaping is needed.
        var db       = c.Db;
        var appUser  = c.AppUser;
        var appPwd   = c.AppPassword.Replace("'", "''");   // SQL single-quote escape only

        var sql =
            // Batch 1: create the database (must stand alone)
            $"IF DB_ID(N'{db}') IS NULL CREATE DATABASE [{db}];\n" +
            "GO\n" +
            // Batch 2: create server-level login (must precede USE)
            $"IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'{appUser}')\n" +
            $"    CREATE LOGIN [{appUser}] WITH PASSWORD=N'{appPwd}', CHECK_POLICY=OFF;\n" +
            "GO\n" +
            // Batch 3: switch to appdb and create the database-level user + roles
            $"USE [{db}];\n" +
            $"IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'{appUser}')\n" +
            $"    CREATE USER [{appUser}] FOR LOGIN [{appUser}];\n" +
            $"ALTER ROLE db_datareader ADD MEMBER [{appUser}];\n" +
            $"ALTER ROLE db_datawriter ADD MEMBER [{appUser}];\n" +
            $"ALTER ROLE db_ddladmin  ADD MEMBER [{appUser}];";

        if (SeedCatalog.IsTemplate(initialData))
            sql += "\nGO\nUSE [" + db + "];\nGO\n" + SeedCatalog.GetSql(initialData);

        var postReadyExec = new List<string>
        {
            "/opt/mssql-tools18/bin/sqlcmd",
            "-C", "-S", "127.0.0.1",
            "-U", c.AdminUser,
            "-P", c.AdminPassword,
            "-Q", sql
        };

        return new EngineSetup(Array.Empty<(string, string)>(), PostReadyExec: postReadyExec);
    }

    public IList<string> ReadinessExec(EngineCredentials c) =>
        new List<string>
        {
            "/opt/mssql-tools18/bin/sqlcmd",
            "-C", "-S", "127.0.0.1",
            "-U", c.AdminUser, "-P", c.AdminPassword,
            "-Q", "SELECT 1"
        };

    public void ApplyHardening(HostConfig hc)
    {
        // mssql writes to many paths at runtime (data, logs, tempdb, secrets); rootfs must be
        // writable. No tmpfs needed — the writable rootfs layer handles transient paths.
        hc.ReadonlyRootfs = false;
        hc.Memory    = 2L * 1024 * 1024 * 1024;   // 2 GB (mssql minimum; hard floor)
        hc.NanoCPUs  = 1_000_000_000L;             // 1 CPU
        hc.PidsLimit = 500L;
        hc.CapDrop   = new List<string> { "ALL" };
        // mssql image runs as non-root 'mssql'. The sqlservr binary has file capability
        // cap_net_bind_service=ep. With no-new-privileges set, the kernel enforces that any
        // file capability on the exec target must be satisfiable within the bounding set;
        // if NET_BIND_SERVICE is absent from the bounding set, exec fails with EPERM.
        // We therefore add NET_BIND_SERVICE in addition to the standard pg/mysql mirror set.
        // SYS_PTRACE and IPC_LOCK are NOT needed (empirically verified).
        hc.CapAdd    = new List<string>
        {
            "CHOWN", "SETUID", "SETGID", "FOWNER", "DAC_OVERRIDE",
            "NET_BIND_SERVICE"  // required: sqlservr has cap_net_bind_service=ep file capability
        };
        hc.SecurityOpt = new List<string> { "no-new-privileges" };
    }
}
