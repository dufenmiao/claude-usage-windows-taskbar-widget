using System.Runtime.InteropServices;

namespace ClaudeUsageWidgetProvider;

/// <summary>
/// Keeps the widget window topmost using WinEvent hooks on a dedicated background thread.
/// Hook callbacks only set a flag and post WM_USER — SetWindowPos runs on the hook thread,
/// never blocking input or the UI thread.
/// </summary>
internal sealed class TopMostEnforcer : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x, pt_y;
    }

    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint WM_QUIT = 0x0012;
    private const uint WM_USER = 0x0400;
    private const uint WM_REASSERT = WM_USER + 1; // SetWindowPos only, no further scheduling
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;

    private readonly IntPtr _hwnd;
    private volatile uint _threadId;
    private volatile bool _paused;
    private bool _disposed;

    // Must be a field — local variable gets GC'd in Release builds before the message loop ends
    private readonly WinEventDelegate _fgDelegate;

    internal TopMostEnforcer(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _fgDelegate = OnForegroundChanged;
        var thread = new Thread(Run) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();

        var fgHook = SetWinEventHook(EventSystemForeground, EventSystemForeground,
            IntPtr.Zero, _fgDelegate, 0, 0, WinEventOutOfContext);

        int ret;
        while ((ret = GetMessage(out var msg, IntPtr.Zero, 0, 0)) != 0)
        {
            if (ret == -1) break;
            if (msg.message == WM_USER)
            {
                SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
                ScheduleFollowUps();
                continue;
            }
            if (msg.message == WM_REASSERT)
            {
                SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
                continue;
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (fgHook != IntPtr.Zero) UnhookWinEvent(fgHook);
    }

    internal void Pause()  => _paused = true;
    internal void Resume() => _paused = false;

    private void RequestTopmost() => PostThreadMessage(_threadId, WM_USER, IntPtr.Zero, IntPtr.Zero);

    // When the taskbar is activated (clicked), it raises itself a moment AFTER the foreground event,
    // so a single SetWindowPos loses the race and the widget stays hidden under the taskbar.
    // So after each event we repeat it a few more times with a delay.
    private static readonly int[] FollowUpDelaysMs = [100, 250, 600, 1200];

    private void ScheduleFollowUps()
    {
        _ = Task.Run(async () =>
        {
            int elapsed = 0;
            foreach (var at in FollowUpDelaysMs)
            {
                await Task.Delay(at - elapsed);
                elapsed = at;
                if (_disposed || _paused) return;
                PostThreadMessage(_threadId, WM_REASSERT, IntPtr.Zero, IntPtr.Zero);
            }
        });
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (_paused) return;
        RequestTopmost();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0)
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
