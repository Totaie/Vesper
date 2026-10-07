using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vesper.Interop;

namespace Vesper.Services;

internal readonly record struct PhysicalKey(uint VirtualKey, uint ScanCode, bool Extended, bool Down);

/// <summary>
/// A low-level keyboard hook that exists only while a session window is in front with its screen
/// focused, so keys like the Windows key and Alt+Tab can be sent to the Mac instead of Windows.
/// It is removed the moment the session loses focus. The handler decides what to swallow; nothing
/// is recorded.
/// </summary>
internal sealed unsafe class KeyboardHook : IDisposable
{
    private static KeyboardHook? s_current;
    private nint _hook;

    /// <summary>Called on the UI thread for every key while installed. Return true to keep the key from Windows.</summary>
    public Func<PhysicalKey, bool>? Handler { get; set; }

    public bool IsInstalled => _hook != 0;

    public bool Install()
    {
        if (_hook != 0) return true;
        s_current?.Uninstall();
        s_current = this;
        _hook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, &HookProc, Native.GetModuleHandle(null), 0);
        if (_hook == 0)
        {
            s_current = null;
            return false;
        }
        return true;
    }

    public void Uninstall()
    {
        if (_hook != 0)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
        if (s_current == this) s_current = null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint HookProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && s_current?.Handler is { } handler)
        {
            var info = (Native.KbdLlHookStruct*)lParam;
            bool down = wParam is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
            try
            {
                if (handler(new PhysicalKey(info->VkCode, info->ScanCode, (info->Flags & Native.LLKHF_EXTENDED) != 0, down)))
                {
                    return 1;
                }
            }
            catch
            {
                // Never let an exception escape into the hook chain.
            }
        }
        return Native.CallNextHookEx(0, code, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
