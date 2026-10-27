using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AirDrop.App.Interop;

/// <summary>System-wide hotkey bound to a window handle (works while the window is hidden).</summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hwnd;
    private readonly int _id;
    private readonly HwndSource _src;
    private readonly Action _handler;

    public bool IsRegistered { get; }

    public GlobalHotkey(Window w, int id, uint mods, uint vk, Action handler)
    {
        _hwnd = new WindowInteropHelper(w).EnsureHandle();
        _id = id;
        _handler = handler;
        _src = HwndSource.FromHwnd(_hwnd);
        _src.AddHook(Hook);
        IsRegistered = RegisterHotKey(_hwnd, id, mods | MOD_NOREPEAT, vk);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
        {
            handled = true;
            _handler();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterHotKey(_hwnd, _id);
        _src.RemoveHook(Hook);
    }
}
