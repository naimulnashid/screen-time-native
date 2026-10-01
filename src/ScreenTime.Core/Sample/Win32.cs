using System.Runtime.InteropServices;
using System.Text;

namespace ScreenTime.Core.Sample;

/// <summary>
/// The Windows calls the sampler makes. None needs elevation: they read this
/// session's own foreground window, input clock and lock state.
/// </summary>
internal static class Win32
{
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr hWnd, EnumWindowsProc cb, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint CbSize;
        public uint DwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr hServer, uint sessionId, int infoClass, out IntPtr ppBuffer, out uint pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>
    /// The process that really owns a window, past the UWP frame host.
    /// ApplicationFrameHost.exe hosts every store app: the frame belongs to the
    /// host while the app owns a CHILD window. The first child owned by a
    /// different process is the app.
    /// </summary>
    internal static uint RealProcessId(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out var framePid);
        var found = framePid;
        EnumChildWindows(hWnd, (child, _) =>
        {
            GetWindowThreadProcessId(child, out var childPid);
            if (childPid != 0 && childPid != framePid)
            {
                found = childPid;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// The full image path of a process, or null where Windows refuses it.
    /// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> is granted for nearly every
    /// process in the session, unelevated - more than the web sampler's
    /// <c>Process.Path</c>, which opens the process for full reading.
    /// </summary>
    internal static string? ImagePath(uint pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var size = 1024u;
            var buffer = new StringBuilder((int)size);
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Authoritative lock state for THIS session: 0 locked, 1 unlocked, -1
    /// Windows does not know, -2 the call failed.
    /// </summary>
    /// <remarks>
    /// <c>WTSQuerySessionInformation(WTSSessionInfoEx)</c> is the documented
    /// API and needs no elevation for one's own session. <c>WTSINFOEXW</c> is
    /// <c>{ DWORD Level; WTSINFOEX_LEVEL1_W Data; }</c>, and Data starts at
    /// offset 8, not 4: LEVEL1 holds LARGE_INTEGER members further down, which
    /// give it 8-byte alignment. Inside it come SessionId, SessionState and
    /// SessionFlags, so SessionFlags sits at 8 + 8 = 16. Verified on a raw dump
    /// by the web sampler, where the bytes at 20 spelled the start of
    /// WinStationName.
    /// </remarks>
    internal static int SessionLockState()
    {
        // WTS_CURRENT_SERVER_HANDLE = 0, WTS_CURRENT_SESSION = (DWORD)-1, WTSSessionInfoEx = 25.
        if (!WTSQuerySessionInformationW(IntPtr.Zero, 0xFFFFFFFF, 25, out var buf, out var got)) return -2;
        try
        {
            return got < 20 ? -2 : Marshal.ReadInt32(buf, 16);
        }
        finally
        {
            if (buf != IntPtr.Zero) WTSFreeMemory(buf);
        }
    }

    /// <summary>
    /// Milliseconds since the last keyboard or mouse input. Unsigned on both
    /// sides, so the subtraction survives the tick count wrapping every 49.7 days.
    /// </summary>
    internal static long IdleMs()
    {
        var info = new LastInputInfo { CbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return unchecked((uint)Environment.TickCount - info.DwTime);
    }
}
