using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace MayFly.Api.Engines;

public sealed class SqlServerEngineClient : IEngineClient
{
    public string EngineId => "mssql";
    public int Port => 1433;

    public DbConnection CreateConnection(string adoConnectionString)
        => new SqlConnection(adoConnectionString);

    public string BuildAdoConnectionString(string host, int port, string db, string user, string password)
        => new SqlConnectionStringBuilder
        {
            DataSource = $"{host},{port}",
            InitialCatalog = db,
            UserID = user,
            Password = password,
            // Encrypt=Optional and TrustServerCertificate are deliberate: traffic runs over the
            // internal mayfly-users network where there is no PKI; encryption is opportunistic.
            TrustServerCertificate = true,
            ConnectTimeout = 5,
            CommandTimeout = 10,
            Encrypt = SqlConnectionEncryptOption.Optional
        }.ToString();

    public string BuildDisplayConnectionString(string host, int port, string db, string user, string password)
        => $"Server={host},{port};Database={db};User Id={user};Password={password};TrustServerCertificate=True";

    public string SizeQuerySql(string db)
        => $"SELECT CAST(COALESCE(SUM(size),0) AS BIGINT)*8192 FROM sys.master_files WHERE database_id=DB_ID('{db}')";

    public string SoftEnforceReadOnlySql(string appUser, string db)
    {
        // SQL Server flips the DATABASE read-only, so the app user is not referenced here.
        _ = appUser;
        return $"USE [master]; ALTER DATABASE [{db}] SET READ_ONLY WITH ROLLBACK IMMEDIATE;";
    }
}
