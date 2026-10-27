using System.Runtime.InteropServices;

namespace Nitro.Continuity.Platform;

/// <summary>Whether this process runs with MSIX package identity (needed for PhoneLine / phoneLineTransportManagement).</summary>
public static class PackageIdentity
{
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint length, char[]? fullName);

    private static readonly Lazy<string?> _fullName = new(() =>
    {
        try
        {
            uint len = 0;
            if (GetCurrentPackageFullName(ref len, null) == AppModelErrorNoPackage) return null;
            var buf = new char[len];
            return GetCurrentPackageFullName(ref len, buf) == 0 ? new string(buf, 0, (int)Math.Max(0, len - 1)) : null;
        }
        catch (EntryPointNotFoundException) { return null; }
    });

    public static bool HasIdentity => _fullName.Value is not null;
    public static string? FullName => _fullName.Value;
}
