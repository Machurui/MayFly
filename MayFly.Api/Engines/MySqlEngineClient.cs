using System.Data.Common;
using MySqlConnector;

namespace MayFly.Api.Engines;

public class MySqlEngineClient : IEngineClient
{
    public virtual string EngineId => "mysql";
    public int Port => 3306;

    public DbConnection CreateConnection(string adoConnectionString)
        => new MySqlConnection(adoConnectionString);

    public string BuildAdoConnectionString(string host, int port, string db, string user, string password)
        => new MySqlConnectionStringBuilder
        {
            Server = host,
            Port = (uint)port,
            Database = db,
            UserID = user,
            Password = password,
            ConnectionTimeout = 5,
            DefaultCommandTimeout = 10,
            AllowPublicKeyRetrieval = true,
            SslMode = MySqlSslMode.None
        }.ToString();

    public string BuildDisplayConnectionString(string host, int port, string db, string user, string password)
        => $"mysql://{user}:{password}@{host}:{port}/{db}";

    public string SizeQuerySql(string db)
        => $"SELECT COALESCE(SUM(data_length+index_length),0) FROM information_schema.tables WHERE table_schema='{db}'";

    public string SoftEnforceReadOnlySql(string appUser, string db)
        => $"REVOKE INSERT, UPDATE, DELETE, CREATE, DROP, ALTER, INDEX ON `{db}`.* FROM '{appUser}'@'%'; FLUSH PRIVILEGES;";
}
