using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace AirDrop.App.Services;

/// <summary>Real HKCU and SendTo for <see cref="ExplorerMenuService"/> (app only; tests use fakes).</summary>
internal static class ExplorerMenuIntegration
{
    private static ExplorerMenuService? _default;

    public static ExplorerMenuService Default => _default ??=
        new ExplorerMenuService(new Win32UserRegistry(), new ShellLinkStore(), Environment.ProcessPath!, PackagedApp.IsPackaged);
}

internal sealed class Win32UserRegistry : IUserRegistry
{
    public string? GetString(string subKey, string? name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(subKey);
        return k?.GetValue(name) as string;
    }

    public void SetString(string subKey, string? name, string value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
        k.SetValue(name, value, RegistryValueKind.String);
    }

    public void DeleteTree(string subKey) => Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
}

/// <summary>.lnk via the shell's ShellLink coclass (IShellLinkW + IPersistFile).</summary>
internal sealed class ShellLinkStore : IShortcutStore
{
    public string SendToFolder => Environment.GetFolderPath(Environment.SpecialFolder.SendTo);

    public bool Exists(string path) => File.Exists(path);

    public void Delete(string path) => File.Delete(path);

    public void Create(string path, string target, string arguments, string description)
    {
        var link = (IShellLinkW)new CShellLink();
        try
        {
            link.SetPath(target);
            link.SetArguments(arguments);
            link.SetDescription(description);
            link.SetIconLocation(target, 0);
            link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? "");
            ((IPersistFile)link).Save(path, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
