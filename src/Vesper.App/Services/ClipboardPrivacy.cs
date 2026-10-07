using Vesper.Interop;

namespace Vesper.Services;

internal enum ClipboardMark { Shareable, Private, Busy }

/// <summary>
/// Reads the marks an app leaves on the Windows clipboard to say "don't pass this on". Password
/// managers set them when copying a password, and Windows' own clipboard history and cloud sync
/// honour them; so does Vesper.
/// </summary>
internal static class ClipboardPrivacy
{
    private static readonly uint ExcludeFromMonitors = Native.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint CanIncludeInHistory = Native.RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint CanUploadToCloud = Native.RegisterClipboardFormat("CanUploadToCloudClipboard");

    public static unsafe ClipboardMark Check()
    {
        // Its mere presence is the request.
        if (ExcludeFromMonitors != 0 && Native.IsClipboardFormatAvailable(ExcludeFromMonitors)) return ClipboardMark.Private;

        bool history = CanIncludeInHistory != 0 && Native.IsClipboardFormatAvailable(CanIncludeInHistory);
        bool cloud = CanUploadToCloud != 0 && Native.IsClipboardFormatAvailable(CanUploadToCloud);
        if (!history && !cloud) return ClipboardMark.Shareable;

        // These two carry a DWORD: 0 means "keep it out".
        if (!Native.OpenClipboard(0)) return ClipboardMark.Busy;
        try
        {
            return (history && IsZero(CanIncludeInHistory)) || (cloud && IsZero(CanUploadToCloud))
                ? ClipboardMark.Private
                : ClipboardMark.Shareable;
        }
        finally
        {
            Native.CloseClipboard();
        }

        static bool IsZero(uint format)
        {
            nint handle = Native.GetClipboardData(format);
            if (handle == 0) return true; // unreadable: assume the stricter meaning
            nint p = Native.GlobalLock(handle);
            if (p == 0) return true;
            try
            {
                return Native.GlobalSize(handle) < 4 || *(uint*)p == 0;
            }
            finally
            {
                Native.GlobalUnlock(handle);
            }
        }
    }
}
