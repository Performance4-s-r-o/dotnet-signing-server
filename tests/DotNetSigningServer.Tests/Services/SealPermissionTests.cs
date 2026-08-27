using DotNetSigningServer.Models;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// Sealing signs with the operator's own certificate, so unlike every other
/// endpoint a valid token and a credit balance are not enough for it. The whole
/// control is one flag that has to start switched off — sign-up is open and hands
/// out free credits, so anything that defaults to "allowed" is immediately
/// reachable by anyone who registers.
/// </summary>
public class SealPermissionTests
{
    [Fact]
    public void NewAccount_CannotSeal()
    {
        Assert.False(new User().SealAllowed);
    }

    [Fact]
    public void PayingAndPrivilegedAccounts_StillCannotSealByThemselves()
    {
        // None of these imply the grant: paying for credits buys signing with
        // material the caller supplied, and being an admin of one's own account
        // says nothing about borrowing our identity.
        var user = new User
        {
            IsActive = true,
            EmailVerified = true,
            IsEnterprise = true,
            IsAdmin = true,
            CreditsRemaining = 1000,
        };

        Assert.False(user.SealAllowed);
    }
}
