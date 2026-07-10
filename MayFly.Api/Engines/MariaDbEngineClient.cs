namespace MayFly.Api.Engines;

public class MariaDbEngineClient : MySqlEngineClient
{
    public override string EngineId => "mariadb";
}
