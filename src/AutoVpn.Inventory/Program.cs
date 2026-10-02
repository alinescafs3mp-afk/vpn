using System.Text.Json;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Fetch;

var treePath = Arg(args, "--tree");
var outPath = Arg(args, "--out");
var fetch = Arg(args, "--fetch");
string json;
if (!string.IsNullOrWhiteSpace(treePath))
{
    json = await File.ReadAllTextAsync(treePath).ConfigureAwait(false);
}
else if (!string.IsNullOrWhiteSpace(fetch))
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AutoVPN-inventory/0.1");
    client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
    json = await client.GetStringAsync(new Uri($"https://api.github.com/repos/igareck/vpn-configs-for-russia/git/trees/{fetch}?recursive=1")).ConfigureAwait(false);
}
else
{
    Console.Error.WriteLine("Usage: autovpn-inventory --tree <tree.json> --out <report.json> | --fetch <commit> --out <report.json>");
    return 2;
}

var discovery = GithubTreeParser.Parse(json);
var report = new
{
    retrievedUtc = DateTimeOffset.UtcNow.ToString("O"),
    commit = discovery.CommitSha,
    complete = discovery.Complete,
    truncated = discovery.Truncated,
    reason = discovery.ReasonCode,
    pathCount = discovery.Paths.Count,
    byClass = discovery.Paths.GroupBy(item => item.Class.ToString()).ToDictionary(group => group.Key, group => group.Count()),
    byFamily = discovery.Paths.Where(item => item.FamilyId is not null).GroupBy(item => item.FamilyId!).ToDictionary(group => group.Key, group => group.Count()),
    subscriptionPaths = discovery.Paths.Count(item => ArtifactClassifier.IsSubscriptionData(item.Class)),
    note = "Counts only. Subscription bodies, credentials, and runtime profiles are not stored.",
};
var text = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
if (!string.IsNullOrWhiteSpace(outPath))
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
    await File.WriteAllTextAsync(outPath, text + Environment.NewLine).ConfigureAwait(false);
}
else
{
    Console.WriteLine(text);
}

return discovery.Complete ? 0 : 3;

static string? Arg(string[] values, string name)
{
    var index = Array.FindIndex(values, value => string.Equals(value, name, StringComparison.Ordinal));
    return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
}
