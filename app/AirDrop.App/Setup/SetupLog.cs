using System;
using System.IO;

namespace AirDrop.App.Setup;

/// <summary>Append-only setup log (%LOCALAPPDATA%\airdrop-win\setup.log). Never throws.</summary>
public static class SetupLog
{
    private static readonly object Gate = new();

    public static string FilePath { get; set; } = SetupPaths.LogFile;

    /// <summary>"[admin] " in the elevated helper so both processes can share one file.</summary>
    public static string Prefix { get; set; } = "";

    /// <summary>Raised for every line (the wizard shows the tail during install).</summary>
    public static event Action<string>? Line;

    public static void Write(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {Prefix}{message}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (Exception) { /* logging must never break setup */ }
        }
        try { Line?.Invoke(line); } catch (Exception) { }
    }
}
