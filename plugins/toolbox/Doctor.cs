using System.Text.RegularExpressions;

namespace NeoIDE.Plugin.Toolbox;

public sealed class DoctorCheck
{
    public string Section { get; init; } = "";
    public string Status { get; init; } = "";
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public List<string> Hints { get; } = new();
}

/// <summary>Parses "php bin/neo doctor": sections, then "   OK       Name      value" lines and "-> hint" lines.</summary>
public static class Doctor
{
    private static readonly Regex Check = new(@"^\s{2,}(?<s>OK|INFO|WARNING|WARN|ERROR|FAIL|FAILED|SKIP|SKIPPED|NOTE)\s+(?<n>.+?)(?:\s{2,}(?<v>.*))?$", RegexOptions.Compiled);
    private static readonly Regex Hint = new(@"^\s+->\s?(?<h>.*)$", RegexOptions.Compiled);
    private static readonly Regex Section = new(@"^ (?<t>\S.*)$", RegexOptions.Compiled);

    public static (string summary, List<DoctorCheck> checks) Parse(string output)
    {
        var checks = new List<DoctorCheck>();
        var summary = "";
        var section = "";
        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            if (raw.Trim().Length == 0) continue;
            Match m;
            if ((m = Hint.Match(raw)).Success) { if (checks.Count > 0) checks[^1].Hints.Add(m.Groups["h"].Value.Trim()); continue; }
            if ((m = Check.Match(raw)).Success)
            {
                checks.Add(new DoctorCheck { Section = section, Status = Normalize(m.Groups["s"].Value), Name = m.Groups["n"].Value.Trim(), Value = m.Groups["v"].Value.Trim() });
                continue;
            }
            if ((m = Section.Match(raw)).Success)
            {
                if (summary.Length == 0 && checks.Count == 0 && section.Length == 0 && m.Groups["t"].Value.Contains(' ')) summary = m.Groups["t"].Value.Trim();
                else section = m.Groups["t"].Value.Trim();
                continue;
            }
            // continuation of a long value: deeper indentation without status
            if (checks.Count > 0 && raw.StartsWith("     ")) checks[^1].Hints.Add(raw.Trim());
        }
        return (summary, checks);
    }

    private static string Normalize(string s) => s switch { "WARN" => "WARNING", "FAIL" or "FAILED" => "ERROR", "SKIPPED" => "SKIP", "NOTE" => "INFO", _ => s };

    public static string Color(string status) => status switch
    {
        "OK" => "#5FB865", "WARNING" => "#E8A33D", "ERROR" => "#E55B5B", "SKIP" => "#868A91", _ => "#4C9BE8",
    };
}
