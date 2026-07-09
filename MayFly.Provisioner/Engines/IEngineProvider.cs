namespace MayFly.Provisioner.Engines;

public record EngineCredentials(string AdminUser, string AdminPassword, string AppUser, string AppPassword, string Db);
// Setup is EITHER init-script files (placed in the engine's initdb.d) OR a post-ready exec.
public record EngineSetup(IReadOnlyList<(string FileName, string Sql)> InitScripts, IReadOnlyList<string>? PostReadyExec);

public interface IEngineProvider
{
    string EngineId { get; }        // "postgres" | "mysql" | "mariadb" | "mssql"
    string Image { get; }
    int Port { get; }               // 5432 | 3306 | 3306 | 1433
    bool UsesInitVolume { get; }    // true = initdb.d via init-volume/writer; false = post-ready docker-exec (mssql)
    EngineCredentials GenerateCredentials();               // engine-compliant passwords
    IList<string> BuildEnv(EngineCredentials c);           // POSTGRES_*/MYSQL_*/MARIADB_*/MSSQL_*
    EngineSetup BuildSetup(EngineCredentials c, string initialData);
    IList<string> ReadinessExec(EngineCredentials c);      // docker-exec argv returning 0 when TCP-ready
    void ApplyHardening(global::Docker.DotNet.Models.HostConfig hc); // engine mem/rootfs/tmpfs/caps
}
