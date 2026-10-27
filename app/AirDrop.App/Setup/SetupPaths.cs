using System;
using System.IO;

namespace AirDrop.App.Setup;

/// <summary>Where the setup wizard keeps its files. WPF-free (linked into AirDrop.Tests).</summary>
public static class SetupPaths
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "airdrop-win");

    /// <summary>Wizard progress, survives reboots.</summary>
    public static string StateFile => Path.Combine(DataDir, "setup.json");

    /// <summary>Every step and every (dry-run) command is appended here.</summary>
    public static string LogFile => Path.Combine(DataDir, "setup.log");

    /// <summary>Package assembled by the (non-elevated) UI: inf + sys + cat from the app, firmware downloaded.</summary>
    public static string PackageCache => Path.Combine(DataDir, "driver-pkg");

    /// <summary>
    /// Copy the elevated helper signs and installs from. Administrators/SYSTEM only, so a non-elevated process
    /// cannot swap files between signing and pnputil.
    /// </summary>
    public static string ProtectedPackageDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NitroDrop", "driver");

    /// <summary>Driver files shipped next to the executable (inf, sys and the pre-built unsigned cat).</summary>
    public static string BundledDriverDir => Path.Combine(AppContext.BaseDirectory, "driver");
}
