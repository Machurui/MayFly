using System.Security.Cryptography;
using Docker.DotNet.Models;

namespace MayFly.Provisioner.Engines;

public sealed class MySqlEngineProvider : IEngineProvider
{
    public string EngineId => "mysql";
    public string Image => "mysql:8.4";
    public int Port => 3306;
    public bool UsesInitVolume => true;
    public string DataDirectory => "/var/lib/mysql";

    public EngineCredentials GenerateCredentials()
    {
        var adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var appPassword   = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        return new EngineCredentials("root", adminPassword, "appuser", appPassword, "appdb");
    }

    public IList<string> BuildEnv(EngineCredentials c) =>
        new List<string>
        {
            $"MYSQL_ROOT_PASSWORD={c.AdminPassword}",
            $"MYSQL_DATABASE={c.Db}"
        };

    public EngineSetup BuildSetup(EngineCredentials c, string initialData)
    {
        // blank only for SP2 — no northwind for mysql
        var roles =
            $"CREATE USER '{c.AppUser}'@'%' IDENTIFIED BY '{Escape(c.AppPassword)}';\n" +
            $"GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, DROP, INDEX, ALTER, REFERENCES " +
            $"ON `{c.Db}`.* TO '{c.AppUser}'@'%';\n" +
            "FLUSH PRIVILEGES;\n";

        return new EngineSetup(new[] { ("00-roles.sql", roles) }, PostReadyExec: null);
    }

    /// <summary>
    /// Probe TCP on 127.0.0.1 (not the unix socket) so the readiness check is NOT satisfied
    /// until the real mysqld server binds port 3306 — the temporary init-only server uses the
    /// unix socket with --skip-networking, so this is the correct "genuinely ready" signal.
    /// </summary>
    public IList<string> ReadinessExec(EngineCredentials c) =>
        new List<string> { "mysqladmin", "ping", "-h", "127.0.0.1", "-uroot", $"-p{c.AdminPassword}" };

    public void ApplyHardening(HostConfig hc)
    {
        hc.ReadonlyRootfs = true;
        // MySQL 8.4 writes pid/socket to /var/run/mysqld, uses /tmp and /run at runtime.
        // /run is needed for systemd-style socket dirs that the OL8 mysql image touches.
        // Memory floor is 512 MB (higher than pg because InnoDB buffer pool default is 128 MB
        // and MySQL needs more headroom than pg for first-init).
        hc.Tmpfs = new Dictionary<string, string>
        {
            ["/tmp"]            = "rw,noexec,nosuid,size=64m",
            ["/var/run/mysqld"] = "rw,noexec,nosuid,size=16m",
            ["/run"]            = "rw,noexec,nosuid,size=16m"
        };
        hc.Memory     = 512L * 1024 * 1024;
        hc.NanoCPUs   = 500_000_000L;
        hc.PidsLimit  = 200L;
        hc.CapDrop    = new List<string> { "ALL" };
        hc.CapAdd     = new List<string> { "CHOWN", "SETUID", "SETGID", "FOWNER", "DAC_OVERRIDE" };
        hc.SecurityOpt = new List<string> { "no-new-privileges" };
    }

    private static string Escape(string s) => s.Replace("'", "''");
}
