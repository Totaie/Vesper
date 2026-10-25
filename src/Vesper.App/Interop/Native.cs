using System.Runtime.InteropServices;

namespace Vesper.Interop;

/// <summary>The Win32 calls Vesper needs, and nothing else.</summary>
internal static unsafe partial class Native
{
    // ── Clipboard ──────────────────────────────────────────────────────────

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterClipboardFormat(string name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(nint owner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    public static partial nint GetClipboardData(uint format);

    [LibraryImport("user32.dll")]
    public static partial uint GetClipboardSequenceNumber();

    [LibraryImport("kernel32.dll")]
    public static partial nint GlobalLock(nint mem);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(nint mem);

    [LibraryImport("kernel32.dll")]
    public static partial nuint GlobalSize(nint mem);

    // ── Windows ────────────────────────────────────────────────────────────

    public const uint WDA_NONE = 0x0, WDA_EXCLUDEFROMCAPTURE = 0x11;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindow(string? className, string? windowName);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial short GetKeyState(int vk);

    [LibraryImport("user32.dll")]
    public static partial nint GetKeyboardLayout(uint threadId);

    [LibraryImport("user32.dll")]
    public static partial int ToUnicodeEx(uint vk, uint scanCode, byte* keyState, char* buffer, int bufferSize, uint flags, nint hkl);

    [LibraryImport("user32.dll")]
    public static partial uint MapVirtualKeyExW(uint code, uint mapType, nint hkl);

    // ── Low-level keyboard hook ────────────────────────────────────────────

    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    public const uint LLKHF_EXTENDED = 0x01, LLKHF_INJECTED = 0x10, LLKHF_UP = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    public struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetWindowsHookExW(int hookId, delegate* unmanaged[Stdcall]<int, nint, nint, nint> proc, nint module, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    public static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandle(string? name);

    // ── Cursors ────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct IconInfo
    {
        public int IsIcon;
        public int HotspotX;
        public int HotspotY;
        public nint MaskBitmap;
        public nint ColorBitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapV5Header
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
        public uint RedMask;
        public uint GreenMask;
        public uint BlueMask;
        public uint AlphaMask;
        public int CSType;
        public fixed int Endpoints[9];
        public int GammaRed;
        public int GammaGreen;
        public int GammaBlue;
        public int Intent;
        public int ProfileData;
        public int ProfileSize;
        public int Reserved;
    }

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateDIBSection(nint hdc, BitmapV5Header* header, uint usage, void** bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateBitmap(int width, int height, uint planes, uint bitCount, void* bits);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint obj);

    [LibraryImport("user32.dll")]
    public static partial nint CreateIconIndirect(IconInfo* info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyCursor(nint cursor);

    // ── Process hardening ──────────────────────────────────────────────────

    [LibraryImport("kernel32.dll", EntryPoint = "SetDllDirectoryW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetDllDirectory(string path);

    public const int ProcessImageLoadPolicy = 10, ProcessExtensionPointDisablePolicy = 6;

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessMitigationPolicy(int policy, void* buffer, nuint length);

    [LibraryImport("wer.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int WerAddExcludedApplication(string exeName, [MarshalAs(UnmanagedType.Bool)] bool allUsers);

    [LibraryImport("kernel32.dll")]
    public static partial uint SetErrorMode(uint mode);

    public const uint SEM_FAILCRITICALERRORS = 0x1, SEM_NOGPFAULTERRORBOX = 0x2;
}
