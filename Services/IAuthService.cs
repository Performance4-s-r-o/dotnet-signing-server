using DotNetSigningServer.Models;

namespace DotNetSigningServer.Services;

public interface IAuthService
{
    /// <summary>Iteration count applied to new hashes. Store it alongside the hash.</summary>
    int Iterations { get; }

    (byte[] Hash, byte[] Salt, int Iterations) HashPassword(string password);

    /// <summary>
    /// Verifies against the iteration count the hash was produced with. Pass the
    /// value stored on the user record; 0 is treated as the legacy count.
    /// </summary>
    bool VerifyPassword(string password, byte[] hash, byte[] salt, int iterations);
}
