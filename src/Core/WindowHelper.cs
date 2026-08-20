using System.Runtime.InteropServices;

namespace SmartActiveTools.Core;

/// <summary>
/// Win32 helper methods for manipulating window state (bringing to foreground,
/// restoring from minimized state, adjusting Z-order for OCR scanning).
/// </summary>
public static class WindowHelper
{
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    private static readonly nint HWND_TOPMOST = new(-1);
    private static readonly nint HWND_NOTOPMOST = new(-2);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private const byte VK_MENU = 0x12; // Alt key
    private const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>
    /// Restores the window if minimized, brings it to the top of all windows in Z-order,
    /// and grants it foreground focus so screen OCR can capture it cleanly.
    /// </summary>
    /// <param name="hWnd">Window handle</param>
    /// <param name="stayTopmost">If true, keeps the window pinned above other windows.</param>
    /// <returns>True if the window is now the foreground window.</returns>
    public static bool EnsureWindowVisibleAndForeground(nint hWnd, bool stayTopmost = false)
    {
        if (hWnd == nint.Zero || !IsWindow(hWnd))
            return false;

        // 1. If the window is minimized, restore it
        if (IsIconic(hWnd))
        {
            ShowWindow(hWnd, SW_RESTORE);
        }
        else
        {
            ShowWindow(hWnd, SW_SHOW);
        }

        // 2. Bypass Windows foreground lock using thread attachment + Alt key simulation
        nint fgWindow = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fgWindow, out _);
        uint targetThread = GetWindowThreadProcessId(hWnd, out _);
        uint currentThread = GetCurrentThreadId();

        bool attachedFg = false;
        bool attachedTarget = false;

        if (fgThread != currentThread)
            attachedFg = AttachThreadInput(currentThread, fgThread, true);

        if (targetThread != currentThread)
            attachedTarget = AttachThreadInput(currentThread, targetThread, true);

        try
        {
            // Simulate an ALT key press to lift Windows SetForegroundWindow restrictions
            keybd_event(VK_MENU, 0, 0, 0);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);

            // 3. Bring window to the topmost Z-order
            SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

            if (!stayTopmost)
            {
                // Reset from HWND_TOPMOST so other apps can still be used normally later,
                // while keeping the target window at the very top of normal windows.
                SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            }

            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attachedTarget)
                AttachThreadInput(currentThread, targetThread, false);
            if (attachedFg)
                AttachThreadInput(currentThread, fgThread, false);
        }

        // 4. Give the DWM compositor a brief moment to finish redrawing before OCR capture
        Thread.Sleep(80);

        return GetForegroundWindow() == hWnd;
    }

    #region Win32 P/Invoke

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

    #endregion
}
