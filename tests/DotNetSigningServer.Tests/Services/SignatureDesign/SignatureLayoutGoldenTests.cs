using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DotNetSigningServer.Services.SignatureDesign;

namespace DotNetSigningServer.Tests.Services.SignatureDesign;

/// <summary>
/// The C# layout engine against the golden cases of the portal (lib/signature-layout/golden/cases.json,
/// copied here as golden-cases.json): same input, same rects. A difference means the two engines drifted.
/// </summary>
public class SignatureLayoutGoldenTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Both engines round the same way (R2 = Math.round), so the numbers must match exactly.
    private const double Tolerance = 1e-9;

    public static IEnumerable<object[]> Cases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Services", "SignatureDesign", "golden-cases.json");
        var cases = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        foreach (var c in cases) yield return new object[] { c!["name"]!.GetValue<string>() };
    }

    private static JsonObject Case(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Services", "SignatureDesign", "golden-cases.json");
        return JsonNode.Parse(File.ReadAllText(path))!.AsArray().First(c => c!["name"]!.GetValue<string>() == name)!.AsObject();
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Matches_the_portal(string name)
    {
        var c = Case(name);
        var input = c["input"].Deserialize<SignatureLayoutInput>(SignatureLayoutModel.JsonOptions)!;
        Assert.Null(input.Model.Validate());
        var actual = JsonSerializer.SerializeToNode(SignatureLayoutEngine.Compute(input), Json);
        var diff = Compare(c["expected"], actual, "$");
        Assert.True(diff == null, $"{name}: {diff}");
    }

    /// <summary>First difference between two JSON trees; a missing property counts as null.</summary>
    private static string? Compare(JsonNode? expected, JsonNode? actual, string path)
    {
        if (expected == null || actual == null)
            return expected == null && actual == null ? null : $"{path}: expected {expected?.ToJsonString() ?? "null"}, got {actual?.ToJsonString() ?? "null"}";
        switch (expected)
        {
            case JsonObject eo:
                {
                    if (actual is not JsonObject ao) return $"{path}: expected object";
                    foreach (var key in eo.Select(p => p.Key).Union(ao.Select(p => p.Key)))
                    {
                        var d = Compare(eo[key], ao[key], $"{path}.{key}");
                        if (d != null) return d;
                    }
                    return null;
                }
            case JsonArray ea:
                {
                    if (actual is not JsonArray aa) return $"{path}: expected array";
                    if (ea.Count != aa.Count) return $"{path}: expected {ea.Count} items, got {aa.Count}";
                    for (int i = 0; i < ea.Count; i++)
                    {
                        var d = Compare(ea[i], aa[i], $"{path}[{i}]");
                        if (d != null) return d;
                    }
                    return null;
                }
            default:
                {
                    var ev = expected.AsValue();
                    var av = actual.AsValue();
                    if (ev.TryGetValue<double>(out var en) && av.TryGetValue<double>(out var an))
                        return Math.Abs(en - an) <= Tolerance ? null : $"{path}: expected {en}, got {an}";
                    return ev.ToJsonString() == av.ToJsonString() ? null : $"{path}: expected {ev.ToJsonString()}, got {av.ToJsonString()}";
                }
        }
    }
}
