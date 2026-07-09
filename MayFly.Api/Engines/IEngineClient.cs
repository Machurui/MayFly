namespace MayFly.Api.Engines;
using System.Data.Common;

public interface IEngineClient
{
    string EngineId { get; }
    DbConnection CreateConnection(string adoConnectionString);
    string BuildAdoConnectionString(string host, int port, string db, string user, string password);
    string BuildDisplayConnectionString(string host, int port, string db, string user, string password);
    string SizeQuerySql(string db);                    // returns bytes used
    string SoftEnforceReadOnlySql(string appUser, string db);
}
