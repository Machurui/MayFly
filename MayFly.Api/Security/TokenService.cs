using System.Security.Cryptography;
using System.Text;

namespace MayFly.Api.Security;

public sealed class TokenService : ITokenService
{
    public string NewToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public bool Matches(string candidate, string stored)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(stored));
}
