using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using NeoIDE.Sdk;

namespace NeoIDE.Plugins.Shared;

/// <summary>
/// Processes of the plugins (compiled into every plugin, see Directory.Build.props): php, composer, vendor/bin tools,
/// with the output streamed line by line and a clean stop of the whole process tree.
/// </summary>
internal sealed class Proc : IDisposable
{
    private Process? _process;
    public bool IsRunning => _process is { HasExited: false };

    public static string Php(IProject? project) => project?.PhpPath is { Length: > 0 } p && File.Exists(p) ? p : "php";

    /// <summary>composer.phar of the project, of the user (%APPDATA%), of Composer-Setup, or next to composer.bat on the PATH.</summary>
    public static string? ComposerPhar(string root)
    {
        var candidates = new List<string>
        {
            Path.Combine(root, "composer.phar"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Composer", "composer.phar"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ComposerSetup", "bin", "composer.phar"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { if (dir.Length > 0) candidates.Add(Path.Combine(dir.Trim('"'), "composer.phar")); } catch { }
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Quotes one command-line argument (Windows rules).</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"', '&', '|', '<', '>', '^' }) < 0) return arg;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); backslashes = 0; continue; }
            sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>Runs to the end; <paramref name="onLine"/> receives stdout and stderr lines (on a worker thread).</summary>
    public async Task<(int code, string output)> RunAsync(string exe, string args, string cwd, Action<string>? onLine = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["COLUMNS"] = "160";
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["COMPOSER_NO_INTERACTION"] = "1";
        psi.Environment["PHP_CS_FIXER_IGNORE_ENV"] = "1";   // PHP-CS-Fixer refuses PHP versions newer than the ones it knows
        var output = new StringBuilder();
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        void Line(string? l)
        {
            if (l == null) return;
            lock (output) output.AppendLine(l);
            onLine?.Invoke(l);
        }
        p.OutputDataReceived += (_, e) => Line(e.Data);
        p.ErrorDataReceived += (_, e) => Line(e.Data);
        try { p.Start(); }
        catch (Exception ex) { return (-1, $"Cannot start {exe}: {ex.Message}"); }
        _process = p;
        p.StandardInput.Close();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using (ct.Register(Stop))
        {
            try { await p.WaitForExitAsync(); } catch { }
        }
        _process = null;
        lock (output) return (p.HasExited ? p.ExitCode : -1, output.ToString());
    }

    public void Stop()
    {
        try { if (_process is { HasExited: false } p) p.Kill(entireProcessTree: true); } catch { }
    }

    public void Dispose() => Stop();
}

/// <summary>Read-only output pane (monospace, auto-scroll, size-limited).</summary>
internal sealed class OutputPane : TextBox
{
    public OutputPane()
    {
        IsReadOnly = true;
        FontFamily = Ui.Mono;
        FontSize = 12;
        TextWrapping = TextWrapping.NoWrap;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(8, 6, 8, 6);
        this.Themed(BackgroundProperty, "ConsoleBg");
    }

    /// <summary>Thread-safe.</summary>
    public void Append(string line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (Text.Length > 400_000) Text = Text[^200_000..];
            AppendText(line + "\n");
            ScrollToEnd();
        });
    }

    public void Reset(string? header = null) => Dispatcher.Invoke(() => Text = header == null ? "" : header + "\n");
}
