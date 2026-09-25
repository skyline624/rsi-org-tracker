using System.Text.Json.Nodes;

namespace Collector.Tests.TestSupport;

/// <summary>Anonymized RSI captures (see Fixtures/rsi/README.md).</summary>
public static class RsiFixtures
{
    private static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "rsi", name);

    public static string Text(string name) => File.ReadAllText(PathOf(name));

    /// <summary>The raw <c>getOrgMembers</c> JSON answer.</summary>
    public static string MembersJson(string name) => Text(name);

    /// <summary>The roster HTML carried by a <c>getOrgMembers</c> answer.</summary>
    public static string MembersHtml(string name)
        => JsonNode.Parse(Text(name))!["data"]!["html"]!.GetValue<string>();
}
