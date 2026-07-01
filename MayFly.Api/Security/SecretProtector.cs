using Microsoft.AspNetCore.DataProtection;

namespace MayFly.Api.Security;

public interface ISecretProtector { string Protect(string plaintext); string Unprotect(string ciphertext); }

public sealed class SecretProtector : ISecretProtector
{
    private readonly IDataProtector _p;
    public SecretProtector(IDataProtectionProvider provider) => _p = provider.CreateProtector("MayFly.DbSecret");
    public string Protect(string plaintext) => _p.Protect(plaintext);
    public string Unprotect(string ciphertext) => _p.Unprotect(ciphertext);
}
