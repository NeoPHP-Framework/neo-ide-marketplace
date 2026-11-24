using System.Text.Json;
using System.Text.RegularExpressions;

namespace NeoIDE.Plugin.Logs;

public sealed class LogEntry
{
    public string Time { get; init; } = "";
    public string Channel { get; init; } = "";
    public string Level { get; init; } = "";
    public string Message { get; set; } = "";
    public string? Context { get; set; }
    public int Severity => Logs.Severity(Level);
}

public static class Logs
{
    public static readonly string[] Levels = { "DEBUG", "INFO", "NOTICE", "WARNING", "ERROR", "CRITICAL", "ALERT", "EMERGENCY" };

    public static int Severity(string level) => Array.IndexOf(Levels, level.ToUpperInvariant()) is var i and >= 0 ? i : 1;

    public static string Color(string level) => Severity(level) switch
    {
        0 => "#868A91", 1 => "#4C9BE8", 2 => "#2AACB8", 3 => "#E8A33D", 4 => "#E55B5B", _ => "#D63A8A",
    };
}

/// <summary>NeoPHP format: [%datetime%] %channel%.%level% %message% %context% — lines that do not start an entry belong to the previous one.</summary>
public static class LogParser
{
    private static readonly Regex Head = new(@"^\[(?<t>[^\]]{8,40})\]\s+(?<c>[\w.\-]+?)\.(?<l>DEBUG|INFO|NOTICE|WARNING|ERROR|CRITICAL|ALERT|EMERGENCY)\b:?\s?(?<m>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static void Parse(IEnumerable<string> lines, List<LogEntry> into)
    {
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            var m = Head.Match(line);
            if (m.Success)
            {
                var (message, context) = SplitContext(m.Groups["m"].Value);
                into.Add(new LogEntry { Time = m.Groups["t"].Value, Channel = m.Groups["c"].Value, Level = m.Groups["l"].Value.ToUpperInvariant(), Message = message, Context = context });
            }
            else if (into.Count > 0 && line.Length > 0)
            {
                into[^1].Message += "\n" + line;
            }
            else if (line.Length > 0)
            {
                into.Add(new LogEntry { Level = "INFO", Message = line });
            }
        }
    }

    /// <summary>"message {json} [extra]" → message + pretty JSON context.</summary>
    public static (string message, string? context) SplitContext(string text)
    {
        var t = text.TrimEnd();
        // Monolog style trailing "[]" (empty extra)
        if (t.EndsWith(" []")) t = t[..^3].TrimEnd();
        if (t.EndsWith(" []")) t = t[..^3].TrimEnd();
        if (!t.EndsWith('}')) return (t, null);
        for (var i = t.IndexOf(" {", StringComparison.Ordinal); i >= 0; i = t.IndexOf(" {", i + 1, StringComparison.Ordinal))
        {
            var json = t[(i + 1)..];
            try
            {
                using var doc = JsonDocument.Parse(json);
                var pretty = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                return (t[..i], pretty == "{}" ? null : pretty);
            }
            catch (JsonException) { }
        }
        return (t, null);
    }
}
