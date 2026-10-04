using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace AutoVpn.Infrastructure.Core;

/// <summary>Private local runtime contract, not an import format or an IPC payload.</summary>
public sealed record RuntimeProfileContract(int ControllerPort, int SocksPort, [property: JsonIgnore] string ControllerSecret)
{
    public override string ToString() => $"RuntimeProfileContract {{ ControllerPort = {ControllerPort}, SocksPort = {SocksPort}, ControllerSecret = [redacted] }}";

    private static readonly HashSet<string> AllowedRootKeys = new(StringComparer.Ordinal)
    {
        "allow-lan", "bind-address", "mode", "log-level", "ipv6", "find-process-mode",
        "external-controller", "secret", "external-ui", "profile", "listeners", "hosts",
        "dns", "proxies", "proxy-groups", "rules",
    };

    public static RuntimeProfileContract Parse(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml) || yaml.Length > 512 * 1024)
            throw new InvalidDataException("CORE_PROFILE_INVALID");
        try
        {
            // Preflight events before constructing a representation graph: no alias cycles,
            // merge keys, deeply nested documents, or unbounded node counts.
            var parser = new Parser(new StringReader(yaml));
            var depth = 0; var events = 0; var documents = 0;
            while (parser.MoveNext())
            {
                if (++events > 65536) throw new InvalidDataException("CORE_PROFILE_INVALID");
                if (parser.Current is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty))
                    throw new InvalidDataException("CORE_PROFILE_INVALID");
                switch (parser.Current)
                {
                    case AnchorAlias: throw new InvalidDataException("CORE_PROFILE_INVALID");
                    case DocumentStart: if (++documents > 1) throw new InvalidDataException("CORE_PROFILE_INVALID"); break;
                    case MappingStart or SequenceStart:
                        if (++depth > 32) throw new InvalidDataException("CORE_PROFILE_INVALID"); break;
                    case MappingEnd or SequenceEnd: depth--; break;
                }
            }
            var stream = new YamlStream(); stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode map)
                throw new InvalidDataException("CORE_PROFILE_INVALID");
            CheckGraph(map);
            var root = Mapping(map);
            // Presence, not a fragile substring/boolean spelling, gates all top-level TUN.
            if (root.ContainsKey("tun")) throw new InvalidDataException("WINDOWS_TUN_NOT_VALIDATED");
            if (root.Keys.Any(key => !AllowedRootKeys.Contains(key))) throw new InvalidDataException("CORE_PROFILE_INVALID");
            Require(root, "allow-lan", "false"); Require(root, "bind-address", "127.0.0.1");
            Require(root, "mode", "rule"); Require(root, "external-ui", "");
            Require(root, "find-process-mode", "off");
            if (!root.TryGetValue("profile", out var profileNode) || profileNode is not YamlMappingNode profileMap)
                throw new InvalidDataException("CORE_PROFILE_INVALID");
            var storage = Mapping(profileMap);
            if (storage.Count != 1) throw new InvalidDataException("CORE_PROFILE_INVALID");
            Require(storage, "store-selected", "false");
            if (root.TryGetValue("dns", out var dnsNode))
            {
                if (dnsNode is not YamlMappingNode dnsMap) throw new InvalidDataException("CORE_PROFILE_INVALID");
                var dns = Mapping(dnsMap);
                var allowedDns = new[] { "enable", "ipv6", "enhanced-mode", "nameserver", "proxy-server-nameserver" };
                if (dns.Keys.Any(key => !allowedDns.Contains(key, StringComparer.Ordinal)))
                    throw new InvalidDataException("CORE_PROFILE_INVALID");
            }
            var endpoint = Text(root, "external-controller");
            if (!IPEndPoint.TryParse(endpoint, out var controller) ||
                !controller.Address.Equals(IPAddress.Loopback) || controller.Port == 0 ||
                endpoint != "127.0.0.1:" + controller.Port.ToString(CultureInfo.InvariantCulture))
                throw new InvalidDataException("CORE_CONTROLLER_INVALID");
            var secret = Text(root, "secret");
            if (secret.Length is < 32 or > 128 || secret.Any(ch => !char.IsAsciiLetterOrDigit(ch)))
                throw new InvalidDataException("CORE_CONTROLLER_INVALID");
            if (!root.TryGetValue("listeners", out var listeners) || listeners is not YamlSequenceNode sequence ||
                sequence.Children.Count != 1 || sequence.Children[0] is not YamlMappingNode listener)
                throw new InvalidDataException("CORE_LISTENER_INVALID");
            var local = Mapping(listener);
            if (local.Keys.Any(key => key is not ("name" or "type" or "port" or "listen")))
                throw new InvalidDataException("CORE_LISTENER_INVALID");
            Require(local, "type", "socks"); Require(local, "listen", "127.0.0.1");
            if (!int.TryParse(Text(local, "port"), NumberStyles.None, CultureInfo.InvariantCulture, out var socks) ||
                socks is < 1 or > 65535 || socks == controller.Port)
                throw new InvalidDataException("CORE_LISTENER_INVALID");
            return new(controller.Port, socks, secret);
        }
        catch (YamlException) { throw new InvalidDataException("CORE_PROFILE_INVALID"); }
        catch (ArgumentException) { throw new InvalidDataException("CORE_PROFILE_INVALID"); }
    }

    private static void CheckGraph(YamlNode node)
    {
        if (node is YamlMappingNode map)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in map.Children)
            {
                if (pair.Key is not YamlScalarNode key || key.Value is null || key.Value == "<<" || !keys.Add(key.Value))
                    throw new InvalidDataException("CORE_PROFILE_INVALID");
                CheckGraph(pair.Value);
            }
        }
        else if (node is YamlSequenceNode sequence)
            foreach (var child in sequence.Children) CheckGraph(child);
    }

    private static Dictionary<string, YamlNode> Mapping(YamlMappingNode map)
        => map.Children.ToDictionary(pair => ((YamlScalarNode)pair.Key).Value!, pair => pair.Value, StringComparer.Ordinal);
    private static string Text(IReadOnlyDictionary<string, YamlNode> map, string key)
        => map.TryGetValue(key, out var node) && node is YamlScalarNode scalar && scalar.Value is not null
            ? scalar.Value : throw new InvalidDataException("CORE_PROFILE_INVALID");
    private static void Require(IReadOnlyDictionary<string, YamlNode> map, string key, string expected)
    {
        if (Text(map, key) != expected) throw new InvalidDataException("CORE_PROFILE_INVALID");
    }
}
