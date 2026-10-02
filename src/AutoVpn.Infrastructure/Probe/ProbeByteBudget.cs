using System.Globalization;

namespace AutoVpn.Infrastructure.Probe;

/// <summary>
/// Daily probe byte counter shared by later cycles and process restarts.
/// A new UTC day starts again at zero. A corrupt file does not grant a fresh unlimited day
/// beyond the supplied limit; it starts the current day at zero.
/// </summary>
public sealed class ProbeByteBudget
{
    public long Limit { get; }
    public long Spent { get; private set; }
    public DateOnly Day { get; private set; }

    private ProbeByteBudget(DateOnly day, long spent, long limit)
    {
        Day = day;
        Spent = Math.Max(0, spent);
        Limit = limit < 1 ? 1 : limit;
    }

    public static ProbeByteBudget Load(string path, long limit, DateOnly today)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new ProbeByteBudget(today, 0, limit);
            }

            var lines = File.ReadAllLines(path);
            if (lines.Length < 2
                || !DateOnly.TryParseExact(lines[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                || !long.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var spent)
                || spent < 0)
            {
                return new ProbeByteBudget(today, 0, limit);
            }

            return day == today
                ? new ProbeByteBudget(day, spent, limit)
                : new ProbeByteBudget(today, 0, limit);
        }
        catch (IOException)
        {
            return new ProbeByteBudget(today, 0, limit);
        }
    }

    public bool Exhausted(DateOnly today)
    {
        return Day == today && Spent >= Limit;
    }

    public long SpentOn(DateOnly today)
    {
        return Day == today ? Spent : 0;
    }

    public void Charge(int payloadBytes, DateOnly today)
    {
        if (Day != today)
        {
            Day = today;
            Spent = 0;
        }

        Spent += Math.Max(1, payloadBytes);
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\n" + Spent.ToString(CultureInfo.InvariantCulture) + "\n");
        File.Move(temporary, path, overwrite: true);
    }
}
