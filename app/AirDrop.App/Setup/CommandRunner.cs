using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AirDrop.App.Setup;

public sealed record CommandResult(int ExitCode, string Output, bool DryRun = false);

public interface ICommandRunner
{
    bool IsDryRun { get; }
    CommandResult Run(CommandSpec command, bool logOutput = true);
}

/// <summary>Runs commands for real (only ever inside the elevated helper or for a plain reboot).</summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public bool IsDryRun => false;

    public CommandResult Run(CommandSpec c, bool logOutput = true)
    {
        SetupLog.Write($"running: {c}  ({c.Purpose})");
        Encoding enc;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            enc = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception) { enc = Encoding.UTF8; }

        var psi = new ProcessStartInfo(c.FileName, c.Arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = enc,
            StandardErrorEncoding = enc,
        };
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(Timeout))
            {
                try { p.Kill(true); } catch (Exception) { }
                SetupLog.Write("timed out");
                return new CommandResult(-1, "timeout");
            }
            string output = (stdout.Result + stderr.Result).Trim();
            SetupLog.Write($"exit code {p.ExitCode}" + (logOutput && output.Length > 0 ? $": {OneLine(output)}" : ""));
            return new CommandResult(p.ExitCode, output);
        }
        catch (Exception ex)
        {
            SetupLog.Write($"couldn't start: {ex.Message}");
            return new CommandResult(-1, ex.Message);
        }
    }

    private static string OneLine(string s)
    {
        s = s.Replace("\r", "").Replace('\n', ' ');
        return s.Length > 600 ? s[..600] + "…" : s;
    }
}

/// <summary><c>--setup-dry-run</c>: logs what would run, executes nothing, reports success.</summary>
public sealed class DryRunCommandRunner : ICommandRunner
{
    public bool IsDryRun => true;
    public List<CommandSpec> Commands { get; } = new();

    public CommandResult Run(CommandSpec c, bool logOutput = true)
    {
        Commands.Add(c);
        SetupLog.Write($"[dry-run] would run: {c}  ({c.Purpose})");
        return new CommandResult(0, "", DryRun: true);
    }
}
