using System.Security.Cryptography;
using Docker.DotNet.Models;
using MayFly.Provisioner.Seeding;

namespace MayFly.Provisioner.Engines;

public sealed class MongoEngineProvider : IEngineProvider
{
    public string EngineId => "mongo";
    public string Image => "mongo:7.0";
    public int Port => 27017;
    public bool UsesInitVolume => true;
    public string DataDirectory => "/data/db";

    public EngineCredentials GenerateCredentials()
    {
        var adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var appPassword   = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        return new EngineCredentials("mayflyadmin", adminPassword, "appuser", appPassword, "appdb");
    }

    public IList<string> BuildEnv(EngineCredentials c) =>
        new List<string>
        {
            $"MONGO_INITDB_ROOT_USERNAME={c.AdminUser}",
            $"MONGO_INITDB_ROOT_PASSWORD={c.AdminPassword}",
            $"MONGO_INITDB_DATABASE={c.Db}"
        };

    public EngineSetup BuildSetup(EngineCredentials c, string initialData)
    {
        // mongo:7 runs /docker-entrypoint-initdb.d/*.js at first init (against admin db).
        // We create the appuser in the target db with readWrite scope only.
        var js =
            $"db.getSiblingDB('{c.Db}').createUser({{\n" +
            $"  user: '{c.AppUser}', pwd: '{JsEscape(c.AppPassword)}',\n" +
            $"  roles: [{{ role: 'readWrite', db: '{c.Db}' }}]\n" +
            $"}});";

        var scripts = new List<(string, string)> { ("00-roles.js", js) };

        if (SeedCatalog.IsTemplate(initialData))
            scripts.Add(("01-seed.js", SeedCatalog.GetMongoJs(initialData)));

        return new EngineSetup(scripts, PostReadyExec: null);
    }

    /// <summary>
    /// Readiness probe authenticates as admin over TCP so the check is NOT satisfied
    /// by the temporary init-phase server (unix socket / no auth). Mirrors the SQL
    /// engines' approach: we wait for the real authenticated endpoint to be ready.
    /// </summary>
    public IList<string> ReadinessExec(EngineCredentials c) =>
        new List<string>
        {
            "mongosh", "--quiet", "--host", "127.0.0.1",
            "-u", c.AdminUser, "-p", c.AdminPassword,
            "--authenticationDatabase", "admin",
            "--eval", "db.adminCommand('ping')"
        };

    public void ApplyHardening(HostConfig hc)
    {
        hc.ReadonlyRootfs = true;
        // mongo:7 (Debian) writes to /tmp at runtime and to /data/configdb (VOLUME).
        // Both need writable tmpfs under a read-only rootfs.
        hc.Tmpfs = new Dictionary<string, string>
        {
            ["/tmp"]           = "rw,noexec,nosuid,size=64m",
            ["/data/configdb"] = "rw,nosuid,size=64m"
        };
        hc.Memory    = 1024L * 1024 * 1024; // 1 GiB — WiredTiger requires headroom
        hc.NanoCPUs  = 1_000_000_000L;
        hc.PidsLimit = 200L;
        hc.CapDrop   = new List<string> { "ALL" };
        hc.CapAdd    = new List<string> { "CHOWN", "SETUID", "SETGID", "FOWNER", "DAC_OVERRIDE" };
        hc.SecurityOpt = new List<string> { "no-new-privileges" };
    }

    /// <summary>
    /// Escape a value for embedding in a JS string literal (single-quoted).
    /// Hex CSPRNG passwords contain neither backslash nor single-quote, but we
    /// keep the guard to mirror the SQL literal-escape pattern in the other providers.
    /// </summary>
    private static string JsEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("'", "\\'");
}
