using System.Text.Json;
using DotNetSigningServer.Services.Ai;

namespace DotNetSigningServer.Tests.Services.Ai;

/// <summary>The golden failover cases of the AI gateway contract (TestData/ai-failover-cases.json).</summary>
public class AiFailoverTests
{
    public static IEnumerable<object[]> Cases()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TestData", "ai-failover-cases.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "TestData", "ai-failover-cases.json")));
        foreach (var c in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            yield return [c.GetProperty("name").GetString()!, c.GetRawText()];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Case_matches_the_contract(string name, string json)
    {
        using var document = JsonDocument.Parse(json);
        var input = document.RootElement.GetProperty("input");
        var expected = document.RootElement.GetProperty("expected");
        string? Str(string n) => input.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var decision = AiFailover.Classify(new AiFailureInput(
            Str("provider")!,
            input.TryGetProperty("httpStatus", out var s) ? s.GetInt32() : null,
            Str("errorType"),
            Str("message"),
            Str("stopReason"),
            input.TryGetProperty("schemaInvalid", out var si) && si.GetBoolean(),
            Str("transport")));

        Assert.True(expected.GetProperty("action").GetString() == (decision.Switch ? "switch" : "no_switch"), name);
        var report = expected.GetProperty("report");
        Assert.Equal(report.ValueKind == JsonValueKind.Null ? null : report.GetString(), decision.Report);
        Assert.Equal(expected.GetProperty("rule").GetString(), decision.Rule);
    }

    [Fact]
    public void All_contract_cases_are_read() => Assert.True(Cases().Count() >= 19);
}
