using System.Runtime.InteropServices;

namespace Ostraplan.App;

/// <summary>The few Windows calls WPF has no wrapper for, in one place.</summary>
internal static class Win32
{
    /// <summary>For <see cref="AllowSetForegroundWindow"/>: any process may take the foreground.</summary>
    public const int AsfwAny = -1;

    /// <summary>For <see cref="ShowWindow"/>: un-minimise to the size and state the window had before, maximised
    /// included, which setting <c>WindowState.Normal</c> would not do.</summary>
    public const int SwRestore = 9;

    /// <summary>For <see cref="SHChangeNotify"/>: a file type's registration changed.</summary>
    public const int ShcneAssocChanged = 0x08000000;

    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    public static extern bool IsWindowEnabled(IntPtr hwnd);

    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
