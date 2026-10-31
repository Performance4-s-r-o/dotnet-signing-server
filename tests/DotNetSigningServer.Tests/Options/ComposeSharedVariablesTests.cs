using System.Text.RegularExpressions;

namespace DotNetSigningServer.Tests.Options;

/// <summary>
/// A default with its own braces, <c>${NAME:-{{project.X}}}</c>, makes Compose end the default at the
/// first <c>}</c>, so a set variable gets a stray <c>}</c> appended (token, base64 certificate, FQDN).
/// The compose file therefore never names a shared variable in a default; the link to a Coolify shared
/// variable is the application variable's value, <c>{{environment.NAME}}</c>, set in the Coolify UI.
/// </summary>
public class ComposeSharedVariablesTests
{
    private static string Compose()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "docker-compose.yml"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "docker-compose.yml"));
    }

    [Fact]
    public void No_default_carries_double_braces_of_its_own()
    {
        var offenders = Regex.Matches(Compose(), @"\$\{[A-Z0-9_]+:-[^}\r\n]*\{\{")
            .Select(m => m.Value)
            .ToList();
        Assert.True(offenders.Count == 0, "Nested braces in a compose default: " + string.Join(", ", offenders));
    }

    [Fact]
    public void No_shared_placeholder_is_left_in_the_compose_file()
    {
        Assert.DoesNotContain("{{project.", Compose());
        Assert.DoesNotContain("{{environment.", Compose());
    }
}
