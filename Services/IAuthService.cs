using DotNetSigningServer.Models;

namespace DotNetSigningServer.Services;

public interface IAuthService
{
    (byte[] Hash, byte[] Salt) HashPassword(string password);
    bool VerifyPassword(string password, byte[] hash, byte[] salt);
}
