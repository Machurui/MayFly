namespace MayFly.Provisioner.Engines;

public class MariaDbEngineProvider : MySqlEngineProvider
{
    public override string EngineId => "mariadb";
    public override string Image => "mariadb:11.4";

    public override IList<string> BuildEnv(EngineCredentials c) =>
        new List<string>
        {
            $"MARIADB_ROOT_PASSWORD={c.AdminPassword}",
            $"MARIADB_DATABASE={c.Db}"
        };

    public override IList<string> ReadinessExec(EngineCredentials c) =>
        new List<string> { "mariadb-admin", "ping", "-h", "127.0.0.1", "-uroot", $"-p{c.AdminPassword}" };
}
