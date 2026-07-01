namespace MayFly.Api.Security;
public interface ITokenService { string NewToken(); bool Matches(string candidate, string stored); }
