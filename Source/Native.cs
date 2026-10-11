using System.Runtime.InteropServices;

namespace PoGGaduchy;

/// <summary>Dostępny obszar ekranu (bez paska zadań). Można go nadpisać do testów układu, np. dla ekranu 1920x1080.</summary>
public static class Display
{
    public static System.Windows.Rect? Override { get; set; }

    public static System.Windows.Rect WorkArea => Override ?? System.Windows.SystemParameters.WorkArea;

    /// <summary>Zmniejsza okno dialogowe, jeśli nie mieści się na ekranie.</summary>
    public static void FitToScreen(System.Windows.Window w)
    {
        var a = WorkArea;
        w.MinWidth = Math.Min(w.MinWidth, a.Width * 0.9);
        w.MinHeight = Math.Min(w.MinHeight, a.Height * 0.85);
        w.Width = Math.Min(w.Width, a.Width * 0.96);
        w.Height = Math.Min(w.Height, a.Height * 0.94);
    }
}

public static class Native
{
    public const int SW_RESTORE = 9, SW_MAXIMIZE = 3, SW_SHOW = 5;

    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    /// <summary>Wysuwa okno na wierzch także wtedy, gdy Windows zwykle blokuje przejmowanie fokusu (okno w tle).</summary>
    public static void ForceForeground(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        var fg = GetForegroundWindow();
        var fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        var me = GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        SetForegroundWindow(h);
        if (attached) AttachThreadInput(me, fgThread, false);
    }

    /// <summary>Maksymalizuje okno (jeśli jeszcze nie jest zmaksymalizowane) i wysuwa je na wierzch.</summary>
    public static void Maximize(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        if (!IsZoomed(h)) ShowWindow(h, SW_MAXIMIZE);
        ForceForeground(h);
    }
}
