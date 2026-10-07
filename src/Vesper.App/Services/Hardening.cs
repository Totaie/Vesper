using System.Runtime.InteropServices;
using Vesper.Interop;

namespace Vesper.Services;

/// <summary>
/// Process-level defences applied before any window opens. Each one is best-effort: if Windows
/// refuses a policy, Vesper still runs.
/// </summary>
internal static unsafe class Hardening
{
    public static void Apply()
    {
        // The current directory leaves the DLL search order, so a DLL planted beside whatever
        // folder Vesper was started from can't be loaded. (SetDefaultDllDirectories would be
        // stricter, but it stops the Windows App SDK finding its own resources.)
        Native.SetDllDirectory("");

        // No images from network shares or low-integrity locations; System32 first.
        uint imageLoad = 0b111; // NoRemoteImages | NoLowMandatoryLabelImages | PreferSystem32Images
        Native.SetProcessMitigationPolicy(Native.ProcessImageLoadPolicy, &imageLoad, (nuint)sizeof(uint));

        // Refuse legacy injection points: AppInit_DLLs, Winsock LSPs and global window-hook DLLs.
        // Keyloggers that inject via SetWindowsHookEx can't load into Vesper.
        uint extensionPoints = 1; // DisableExtensionPoints
        Native.SetProcessMitigationPolicy(Native.ProcessExtensionPointDisablePolicy, &extensionPoints, (nuint)sizeof(uint));

        // No Windows Error Reporting for Vesper: a crash dump would contain screen contents,
        // keystrokes and decrypted session keys, and WER can upload dumps.
        try
        {
            Native.WerAddExcludedApplication(Path.GetFileName(Environment.ProcessPath ?? "Vesper.exe"), false);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
        }
        Native.SetErrorMode(Native.SEM_FAILCRITICALERRORS | Native.SEM_NOGPFAULTERRORBOX);
    }
}
