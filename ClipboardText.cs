using System.Runtime.InteropServices;

namespace SnmpMibBrowser;

// Places plain text on the clipboard through Win32. WPF's OLE clipboard reports
// CLIPBRD_E_CANT_OPEN when a clipboard watcher (clipboard history, remote desktop)
// opens the clipboard right after a change, even though the text was copied.
// Here the clipboard is opened with a short retry and the result is exact.
internal static class ClipboardText
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);

    public static bool TrySet(IntPtr owner, string text)
    {
        var opened = false;
        for (var attempt = 0; attempt < 20 && !opened; attempt++)
        {
            opened = OpenClipboard(owner);
            if (!opened) Thread.Sleep(25);
        }
        if (!opened) return false;
        try
        {
            if (!EmptyClipboard()) return false;
            var memory = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)((text.Length + 1) * 2));
            if (memory == IntPtr.Zero) return false;
            var target = GlobalLock(memory);
            Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
            Marshal.WriteInt16(target, text.Length * 2, 0);
            GlobalUnlock(memory);
            // On success the system owns the memory; otherwise it must be released here.
            if (SetClipboardData(CF_UNICODETEXT, memory) != IntPtr.Zero) return true;
            GlobalFree(memory);
            return false;
        }
        finally { CloseClipboard(); }
    }
}
