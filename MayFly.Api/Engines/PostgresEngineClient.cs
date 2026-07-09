using System.Data.Common;
using Npgsql;

namespace MayFly.Api.Engines;

public sealed class PostgresEngineClient : IEngineClient
{
    public string EngineId => "postgres";
    public int Port => 5432;

    public DbConnection CreateConnection(string adoConnectionString)
        => new NpgsqlConnection(adoConnectionString);

    public string BuildAdoConnectionString(string host, int port, string db, string user, string password)
        => new NpgsqlConnectionStringBuilder
        {
            Host = host, Port = port, Database = db, Username = user, Password = password,
            Timeout = 5, CommandTimeout = 10
        }.ToString();

    public string BuildDisplayConnectionString(string host, int port, string db, string user, string password)
        => $"postgresql://{user}:{password}@{host}:{port}/{db}";

    public string SizeQuerySql(string db)
        => "SELECT pg_database_size(current_database())";

    public string SoftEnforceReadOnlySql(string appUser, string db)
        => $"ALTER ROLE {appUser} SET default_transaction_read_only = on";
}
