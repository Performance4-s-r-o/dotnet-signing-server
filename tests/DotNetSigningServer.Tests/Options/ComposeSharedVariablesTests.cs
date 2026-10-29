using System.Text.RegularExpressions;

namespace DotNetSigningServer.Tests.Options;

/// <summary>
/// Coolify fills a shared variable only where the compose file names it as
/// <c>${NAME:-{{project.DOTNET_NAME}}}</c>. A plain <c>${NAME:-x}</c> never sees it.
/// </summary>
public class ComposeSharedVariablesTests
{
    private static readonly string[] Shared =
    {
        "TOKEN_SECRET", "SENTRY_DSN", "FQDN_SERVER", "RUN_MIGRATIONS_ON_START", "AI_GOOGLE_API_KEY",
        "P4_BACKOFFICE_MODE", "P4_BACKOFFICE_BILLING_MODE", "P4_BACKOFFICE_DOCS_MODE",
        "P4_BACKOFFICE_CONSENTS_MODE", "P4_BACKOFFICE_PRICING_MODE", "P4_BACKOFFICE_SUPPORT_MODE",
        "P4_BACKOFFICE_WEBHOOK_SECRET",
    };

    private static string Compose()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "docker-compose.yml"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "docker-compose.yml"));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Variable_falls_back_to_the_project_shared_one(string name)
    {
        var compose = Compose();
        var all = Regex.Matches(compose, Regex.Escape("${" + name + ":-")).Count;
        var shared = Regex.Matches(compose, Regex.Escape("${" + name + ":-{{project.DOTNET_" + name + "}}}")).Count;
        Assert.True(all > 0, name + " is not in the compose file");
        Assert.Equal(all, shared);
    }

    public static IEnumerable<object[]> Names() => Shared.Select(n => new object[] { n });
}
