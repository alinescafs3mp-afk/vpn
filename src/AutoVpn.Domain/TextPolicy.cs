using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoVpn.Domain;

public static partial class DisplayName
{
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Без имени";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsControl(ch) || IsBidi(ch) || ch is '<' or '>' or '`' )
            {
                continue;
            }

            builder.Append(ch);
        }

        var text = CollapseSpaces().Replace(builder.ToString(), " ").Trim();
        if (text.Length == 0)
        {
            return "Без имени";
        }

        return text.Length <= 80 ? text : text[..80];
    }

    private static bool IsBidi(char ch)
    {
        return ch is '\u200e' or '\u200f' or '\u202a' or '\u202b' or '\u202c' or '\u202d' or '\u202e'
            or '\u2066' or '\u2067' or '\u2068' or '\u2069';
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseSpaces();
}

public static partial class CountryLabels
{
    private static readonly (string Code, string Name, string[] Keys)[] Known =
    [
        ("AM", "Армения", ["armenia", "армения", "am"]),
        ("DE", "Германия", ["germany", "deutschland", "германия", "de"]),
        ("FI", "Финляндия", ["finland", "финляндия", "fi"]),
        ("FR", "Франция", ["france", "франция", "fr"]),
        ("GB", "Великобритания", ["uk", "united kingdom", "britain", "великобритания", "gb"]),
        ("NL", "Нидерланды", ["netherlands", "нидерланды", "holland", "nl"]),
        ("RU", "Россия", ["russia", "россия", "ru"]),
        ("US", "США", ["usa", "united states", "сша", "us"]),
        ("TR", "Турция", ["turkey", "türkiye", "турция", "tr"]),
        ("KZ", "Казахстан", ["kazakhstan", "казахстан", "kz"]),
        ("SG", "Сингапур", ["singapore", "сингапур", "sg"]),
        ("JP", "Япония", ["japan", "япония", "jp"]),
        ("HK", "Гонконг", ["hong kong", "гонконг", "hk"]),
        ("SE", "Швеция", ["sweden", "швеция", "se"]),
        ("PL", "Польша", ["poland", "польша", "pl"]),
        ("LV", "Латвия", ["latvia", "латвия", "lv"]),
        ("LT", "Литва", ["lithuania", "литва", "lt"]),
        ("EE", "Эстония", ["estonia", "эстония", "ee"]),
        ("CA", "Канада", ["canada", "канада", "ca"]),
        ("CH", "Швейцария", ["switzerland", "швейцария", "ch"]),
    ];

    public static (string Code, string Name)? FromLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var fromFlag = FlagRegion(label);
        var fromText = TextRegion(label);
        if (fromFlag is not null && fromText is not null &&
            !string.Equals(fromFlag.Value.Code, fromText.Value.Code, StringComparison.Ordinal))
        {
            return null;
        }

        return fromFlag ?? fromText;
    }

    public static bool IsConflict(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        var flag = FlagRegion(label);
        var text = TextRegion(label);
        return flag is not null && text is not null &&
               !string.Equals(flag.Value.Code, text.Value.Code, StringComparison.Ordinal);
    }

    private static (string Code, string Name)? FlagRegion(string label)
    {
        string? code = null;
        for (var i = 0; i < label.Length; i++)
        {
            if (!char.IsHighSurrogate(label[i]) || i + 1 >= label.Length || !char.IsLowSurrogate(label[i + 1]))
            {
                continue;
            }

            var pair = char.ConvertToUtf32(label, i);
            if (pair is < 0x1F1E6 or > 0x1F1FF)
            {
                continue;
            }

            var first = (char)('A' + (pair - 0x1F1E6));
            if (i + 2 < label.Length && char.IsHighSurrogate(label[i + 2]))
            {
                var secondPair = char.ConvertToUtf32(label, i + 2);
                if (secondPair is >= 0x1F1E6 and <= 0x1F1FF)
                {
                    var value = $"{first}{(char)('A' + (secondPair - 0x1F1E6))}";
                    if (code is not null && !string.Equals(code, value, StringComparison.Ordinal))
                    {
                        return null;
                    }

                    code = value;
                }
            }
        }

        if (code is null)
        {
            return null;
        }

        var known = Known.FirstOrDefault(item => item.Code == code);
        return known.Code is null ? (code, code) : (known.Code, known.Name);
    }

    private static (string Code, string Name)? TextRegion(string label)
    {
        var lower = label.ToLowerInvariant();
        (string Code, string Name)? found = null;
        foreach (var item in Known)
        {
            if (!item.Keys.Any(key => Boundary().IsMatch(lower) && ContainsWord(lower, key)))
            {
                continue;
            }

            if (found is not null && found.Value.Code != item.Code)
            {
                return null;
            }

            found = (item.Code, item.Name);
        }

        return found;
    }

    private static bool ContainsWord(string text, string word)
    {
        var index = 0;
        while ((index = text.IndexOf(word, index, StringComparison.Ordinal)) >= 0)
        {
            var before = index == 0 || !char.IsLetter(text[index - 1]);
            var after = index + word.Length >= text.Length || !char.IsLetter(text[index + word.Length]);
            if (before && after)
            {
                return true;
            }

            index += word.Length;
        }

        return false;
    }

    [GeneratedRegex(".+")]
    private static partial Regex Boundary();
}

public static partial class SecretRedactor
{
    public static string Redact(string? text, IEnumerable<string>? canaries = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var result = UserInfo().Replace(text, "$1//<redacted>@");
        result = JsonSecret().Replace(result, "\"$1\":\"<redacted>\"");
        result = SecretField().Replace(result, "$1<redacted>");
        if (canaries is not null)
        {
            foreach (var canary in canaries)
            {
                if (!string.IsNullOrEmpty(canary))
                {
                    result = result.Replace(canary, "<redacted>", StringComparison.Ordinal);
                }
            }
        }

        return result;
    }

    [GeneratedRegex(@"(?i)\b([a-z][a-z0-9+.-]*:)//[^\s/@]+@", RegexOptions.None, 100)]
    private static partial Regex UserInfo();

    [GeneratedRegex(@"(?i)\b(password|secret|token|uuid|public-key|publickey|short-id|shortid|auth|private-key|controller-secret)(\s*[:=]\s*)([^\s,;]+)", RegexOptions.None, 100)]
    private static partial Regex SecretField();

    [GeneratedRegex("(?i)\"(password|secret|token|uuid|public-key|publickey|short-id|shortid|auth|private-key|controller-secret)\"\\s*:\\s*\"[^\"]*\"", RegexOptions.None, 100)]
    private static partial Regex JsonSecret();
}

public static class TrafficMath
{
    public static double? MegabitsPerSecond(long payloadBytes, TimeSpan measured)
    {
        if (payloadBytes < 0 || measured <= TimeSpan.Zero)
        {
            return null;
        }

        return payloadBytes * 8d / measured.TotalSeconds / 1_000_000d;
    }

    public static string FormatRate(double? megabitsPerSecond)
    {
        if (megabitsPerSecond is null)
        {
            return "Не измерена";
        }

        var mbps = megabitsPerSecond.Value;
        if (mbps >= 1000)
        {
            return $"{(mbps / 1000d).ToString("0.0", CultureInfo.GetCultureInfo("ru-RU"))} Гбит/с";
        }

        return $"{mbps.ToString("0.0", CultureInfo.GetCultureInfo("ru-RU"))} Мбит/с";
    }

    public static string FormatBytes(long bytes)
    {
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        if (bytes < 0)
        {
            return "Не измерена";
        }

        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value.ToString(unit == 0 ? "0" : "0.0", culture)} {units[unit]}";
    }
}

public static class RussianPlurals
{
    public static string Servers(int count)
    {
        var mod10 = count % 10;
        var mod100 = count % 100;
        var word = mod10 == 1 && mod100 != 11 ? "сервер"
            : mod10 is >= 2 and <= 4 && mod100 is not (>= 12 and <= 14) ? "сервера"
            : "серверов";
        return $"{count.ToString(CultureInfo.GetCultureInfo("ru-RU"))} {word}";
    }
}
