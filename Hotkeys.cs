using System;
using System.Collections.Generic;
using System.Windows.Interop;

namespace ScreenX;

/// <summary>Classic RegisterHotKey-based global hotkeys on a message-only window.</summary>
public sealed class HotkeyManager : IDisposable
{
    readonly HwndSource source;
    readonly Dictionary<int, Action> actions = new();
    int nextId = 1;

    public HotkeyManager()
    {
        source = new HwndSource(new HwndSourceParameters("screenX.hotkeys") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
        source.AddHook(WndProc);
    }

    public bool Register(uint modifiers, uint vk, Action action)
    {
        int id = nextId++;
        if (!Native.RegisterHotKey(source.Handle, id, modifiers | Native.MOD_NOREPEAT, vk)) return false;
        actions[id] = action;
        return true;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && actions.TryGetValue(wParam.ToInt32(), out var a))
        {
            a();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in actions.Keys) Native.UnregisterHotKey(source.Handle, id);
        source.Dispose();
    }
}
