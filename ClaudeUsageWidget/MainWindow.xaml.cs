using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClaudeUsageWidgetProvider;

/// <summary>
/// Widget window: a single tile (CombinedPanel) with rows of Codex + Claude limits.
/// Either docked in the taskbar left of the tray, or floating (freely draggable, always on top).
/// </summary>
public partial class MainWindow : Window
{
    // ── Win32 P/Invoke ────────────────────────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("shell32.dll")]
    private static extern uint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hwndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public int lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private const uint ABM_GETTASKBARPOS = 0x5u;
    private const uint ABM_GETSTATE = 0x4u;
    private const int ABS_AUTOHIDE = 0x1;
    private const uint ABE_BOTTOM = 3u;
    private const uint MONITOR_DEFAULTTONULL = 0;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    // ── State ─────────────────────────────────────────────────────────────────────

    // accountKey → latest usage; shared between windows (the primary fetches, secondaries only display)
    private static readonly Dictionary<string, UsageData> SharedUsage = [];
    private static event Action? UsageUpdated;

    private readonly List<(ClaudeApiClient Client, UsageData? LastUsage)> _accounts = [];
    private readonly CombinedPanel _panel = new();
    private readonly IntPtr _taskbarHwnd;
    private readonly bool _isPrimary;
    private readonly Dictionary<uint, bool> _isExplorerPidCache = new();

    private DispatcherTimer? _trayWatchTimer;
    private DispatcherTimer? _spinnerTimer;
    private DispatcherTimer? _refreshTimer;
    private DispatcherTimer? _textTimer;
    private DispatcherTimer? _visibilityTimer;
    private TopMostEnforcer? _topMostEnforcer;
    private SettingsWindow? _settingsWindow;
    private bool _fullscreenMode;
    private bool _visibilityCheckRunning;
    private int _lastTrayWidth;
    private int _maxTrayWidthPx; // largest tray width seen so far — see PositionDocked
    private bool _dragging;
    private bool _appliedFloating;
    private double _baseHeight = 48; // taskbar height in DIPs (unscaled tile height)

    internal MainWindow(IntPtr taskbarHwnd, bool isPrimary)
    {
        _taskbarHwnd = taskbarHwnd;
        _isPrimary = isPrimary;
        InitializeComponent();

        RootBorder.Child = _panel;
        RootBorder.MouseRightButtonDown += OnRightButtonDown;
        RootBorder.MouseMove += OnRightDragMove;
        RootBorder.LostMouseCapture += (_, _) => EndRightDrag(save: true);
        _panel.MouseLeftButtonDown += (_, e) =>
        {
            // Floating mode ignores the left button entirely; docked: double-click refreshes.
            if (!IsFloating && e.ClickCount == 2) _ = RefreshAllAsync(force: true);
        };

        ApplyFloatingAppearance();
        ApplyLayout();
        RenderRows();

        SettingsStore.VisibilityChanged += OnSettingsChanged;
        if (!_isPrimary)
            UsageUpdated += OnSharedUsageUpdated;

        Loaded += (_, _) =>
        {
#if DEBUG
            RemoveStartupIfThisExe();
#endif
            SetTaskbarHeight();
            PositionWindow();
            StartTrayWatchTimer();
            StartTextTimer();
            StartVisibilityTimer();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);

        System.Diagnostics.Process.GetCurrentProcess().PriorityClass =
            System.Diagnostics.ProcessPriorityClass.BelowNormal;

        _topMostEnforcer = new TopMostEnforcer(hwnd);
        UpdateCtrlMode();

        Closed += (_, _) =>
        {
            _ctrlTimer?.Stop();
            if (!_isPrimary)
                UsageUpdated -= OnSharedUsageUpdated;
            SettingsStore.VisibilityChanged -= OnSettingsChanged;
            _topMostEnforcer?.Dispose();
            _refreshTimer?.Stop();
            _textTimer?.Stop();
            _visibilityTimer?.Stop();
            _trayWatchTimer?.Stop();
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmMouseActivate)
        {
            handled = true;
            return new IntPtr(MaNoActivate);
        }
        return IntPtr.Zero;
    }

    // ── Data ──────────────────────────────────────────────────────────────────────

    /// <summary>Claude/Codex clients are added after Show() — credential loading (wsl.exe) takes a few seconds.</summary>
    internal async Task AddAccountsAsync(List<ClaudeApiClient> clients)
    {
        foreach (var client in clients)
            _accounts.Add((client, null));
        RenderRows();

        if (!_isPrimary)
        {
            OnSharedUsageUpdated();
            return;
        }

        StartSpinnerTimer();
        await RefreshAllAsync(force: false);
        StopSpinner();
        StartRefreshTimer();
    }

    private static bool IsAccountShown(ClaudeApiClient client) => client.AccountService switch
    {
        ServiceType.Claude => SettingsStore.Instance.ShowClaude,
        ServiceType.Codex => SettingsStore.Instance.ShowCodex,
        _ => true
    };

    private async Task RefreshAllAsync(bool force)
    {
        if (!_isPrimary) return;
        for (int i = 0; i < _accounts.Count; i++)
        {
            var client = _accounts[i].Client;
            if (!IsAccountShown(client)) continue;

            var usage = await client.GetUsageAsync(forceRefresh: force);
            if (usage != null)
            {
                _accounts[i] = (client, usage);
                SettingsStore.Instance.RegisterKnownLabels(client.AccountService, usage);
                NotificationService.Instance.CheckAndNotify(client.AccountKey, usage);
                if (client.AccountKey != null)
                    SharedUsage[client.AccountKey] = usage;
            }
            RenderRows();
        }
        UsageUpdated?.Invoke();
    }

    private void OnSharedUsageUpdated()
    {
        Dispatcher.Invoke(() =>
        {
            for (int i = 0; i < _accounts.Count; i++)
            {
                var client = _accounts[i].Client;
                if (client.AccountKey != null && SharedUsage.TryGetValue(client.AccountKey, out var usage))
                    _accounts[i] = (client, usage);
            }
            RenderRows();
        });
    }

    /// <summary>Rows: Codex first, then Claude; each visible limit = one row. An account without data = error or spinner.</summary>
    private void RenderRows()
    {
        var rows = new List<CombinedPanel.Row>();
        foreach (var service in new[] { ServiceType.Codex, ServiceType.Claude })
        {
            foreach (var (client, lastUsage) in _accounts)
            {
                if (client.AccountService != service || !IsAccountShown(client)) continue;
                if (lastUsage != null)
                {
                    foreach (var limit in lastUsage.Limits)
                        if (!SettingsStore.Instance.IsLimitHidden(limit.Label))
                            rows.Add(new CombinedPanel.Row(service, limit, null));
                }
                else
                {
                    rows.Add(new CombinedPanel.Row(service, null, client.LastError));
                }
            }
        }
        _panel.SetRows(rows);
    }

    // ── Timers ────────────────────────────────────────────────────────────────────

    private void StartSpinnerTimer()
    {
        if (_spinnerTimer != null) return;
        _spinnerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _spinnerTimer.Tick += (_, _) => _panel.AdvanceSpinner();
        _spinnerTimer.Start();
    }

    private void StopSpinner()
    {
        _spinnerTimer?.Stop();
        _spinnerTimer = null;
    }

    private void StartRefreshTimer()
    {
        if (_refreshTimer != null) return;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _refreshTimer.Tick += async (_, _) => await RefreshAllAsync(force: false);
        _refreshTimer.Start();
    }

    /// <summary>The countdown to reset changes even without a new fetch.</summary>
    private void StartTextTimer()
    {
        _textTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _textTimer.Tick += (_, _) => RenderRows();
        _textTimer.Start();
    }

    private void StartTrayWatchTimer()
    {
        _trayWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _trayWatchTimer.Tick += async (_, _) =>
        {
            var trayWidth = await Task.Run(GetTrayWidth);
            if (trayWidth != _lastTrayWidth)
                PositionWindow();
        };
        _trayWatchTimer.Start();
    }

    // ── Settings ──────────────────────────────────────────────────────────────────

    private void OnSettingsChanged()
    {
        Dispatcher.Invoke(() =>
        {
            bool floatingToggled = IsFloating != _appliedFloating;
            ApplyFloatingAppearance(); // also for opacity changes
            if (floatingToggled && !IsFloating) SetTaskbarHeight();
            ApplyLayout();
            RenderRows();

            // A newly enabled service has no data yet → fetch right away.
            if (_isPrimary && _accounts.Any(a => a.LastUsage == null && IsAccountShown(a.Client)))
                _ = RefreshAllAsync(force: false);
        });
    }

    /// <summary>
    /// Window size = tile width × taskbar height, multiplied by FloatingScale in floating mode.
    /// The tile itself is always laid out at its unscaled size; a LayoutTransform scales it up,
    /// so text and icons stay crisp vector rendering.
    /// </summary>
    private void ApplyLayout()
    {
        var settings = SettingsStore.Instance;
        double scale = IsFloating ? settings.FloatingScale / 100.0 : 1.0;
        _panel.SetTileWidth(settings.TileWidth);
        RootBorder.LayoutTransform = scale == 1.0 ? Transform.Identity : new ScaleTransform(scale, scale);
        Width = settings.TileWidth * scale;
        Height = _baseHeight * scale;
        if (IsLoaded) PositionWindow();
    }

    // ── Right button: click = open Settings, hold + drag (floating only) = move ───

    private POINT? _rightDownCursor;   // cursor at right-button down (physical px)
    private RECT _rightDownWindow;     // window rect at right-button down
    private bool _rightDragMoved;
    private const int DragThresholdPx = 4;

    private void OnRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsFloating) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!GetCursorPos(out var p) || !GetWindowRect(hwnd, out var r)) return;
        _rightDownCursor = p;
        _rightDownWindow = r;
        _rightDragMoved = false;
        RootBorder.CaptureMouse();
        e.Handled = true;
    }

    private void OnRightDragMove(object sender, MouseEventArgs e)
    {
        if (_rightDownCursor is not { } start || e.RightButton != MouseButtonState.Pressed) return;
        if (!GetCursorPos(out var p)) return;
        int dx = p.X - start.X, dy = p.Y - start.Y;
        if (!_rightDragMoved)
        {
            if (Math.Abs(dx) < DragThresholdPx && Math.Abs(dy) < DragThresholdPx) return;
            _rightDragMoved = true;
            _dragging = true; // keeps PositionWindow (tray watch timer) from snapping it back
        }
        MoveTo(_rightDownWindow.Left + dx, _rightDownWindow.Top + dy);
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        bool wasDrag = _rightDragMoved;
        EndRightDrag(save: true);
        if (!wasDrag) OpenSettings();
    }

    /// <summary>Finishes a right-button drag; after a real move, keeps the window on-screen and saves its position.</summary>
    private void EndRightDrag(bool save)
    {
        if (_rightDownCursor == null) return;
        bool moved = _rightDragMoved;
        _rightDownCursor = null;   // clear first: ReleaseMouseCapture re-enters via LostMouseCapture
        _rightDragMoved = false;
        _dragging = false;
        if (RootBorder.IsMouseCaptured) RootBorder.ReleaseMouseCapture();

        if (!save || !moved) return;
        if (!GetWindowRect(new WindowInteropHelper(this).Handle, out RECT r)) return;
        var (x, y) = ClampToWorkArea(r.Left, r.Top);
        if (x != r.Left || y != r.Top) MoveTo(x, y);
        var settings = SettingsStore.Instance;
        settings.FloatingX = x;
        settings.FloatingY = y;
        settings.Save();
    }

    private void OpenSettings()
    {
        if (_settingsWindow != null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            PlaceSettingsWindow(_settingsWindow);
            _settingsWindow.Topmost = true;
            _settingsWindow.Activate();
            _settingsWindow.Topmost = false;
            return;
        }
        _settingsWindow = new SettingsWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, // off-screen until we know the size (SizeToContent)
            Top = -10000
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.UpdateLayout();
        PlaceSettingsWindow(_settingsWindow);
        _settingsWindow.Activate();
    }

    /// <summary>
    /// Settings sits right above the widget with right edges aligned. If it doesn't fit above
    /// (widget near the top edge), it goes below; horizontally it is kept inside the virtual screen.
    /// </summary>
    private void PlaceSettingsWindow(Window w)
    {
        const double gap = 6;
        double m = SettingsWindow.ShadowMargin; // the visible card is m smaller than the window
        double left = Left + ActualWidth - w.ActualWidth + m;
        double top = Top - w.ActualHeight - gap + m;
        if (top + m < SystemParameters.VirtualScreenTop)
            top = Top + ActualHeight + gap - m;

        double minLeft = SystemParameters.VirtualScreenLeft;
        double maxLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - w.ActualWidth;
        w.Left = Math.Clamp(left, minLeft, Math.Max(minLeft, maxLeft));
        w.Top = top;
    }

    // ── Floating mode ─────────────────────────────────────────────────────────────
    // Primary window only; click-through unless Ctrl is held, moved by Ctrl + right-drag, and stays above other
    // windows (Topmost + TopMostEnforcer).

    private bool IsFloating => _isPrimary && SettingsStore.Instance.FloatingMode;

    private void ApplyFloatingAppearance()
    {
        _appliedFloating = IsFloating;
        var settings = SettingsStore.Instance;
        // Alpha at least 1: fully transparent pixels of an AllowsTransparency window let the mouse through → dragging wouldn't work.
        byte bgAlpha = (byte)Math.Max(1, settings.FloatingBackgroundOpacity * 255 / 100);
        RootBorder.Background = IsFloating
            ? new SolidColorBrush(Color.FromArgb(bgAlpha, 0x1C, 0x1C, 0x1C))
            : null;
        Opacity = IsFloating ? settings.FloatingWindowOpacity / 100.0 : 1.0;
        // Floating: 1px outline, transparent until Ctrl is held (see UpdateCtrlMode).
        RootBorder.BorderThickness = new Thickness(IsFloating ? 1 : 0);
#if DEBUG
        _panel.Background = IsFloating ? null : new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x00, 0xFF));
#endif
        UpdateCtrlMode();
    }

    // ── Ctrl mode (floating only) ─────────────────────────────────────────────────
    // The floating window is fully click-through (WS_EX_TRANSPARENT): clicks, hover and wheel
    // go to whatever is underneath. Only while Ctrl is held does it accept the mouse
    // (right-drag = move, right-click = Settings). A cheap 50 ms poll of the Ctrl key state
    // toggles this — no global mouse hook needed. A drag in progress stays interactive until
    // the button is released, even if Ctrl is let go first.

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_CONTROL = 0x11;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private static readonly Brush CtrlOutlineBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));

    private DispatcherTimer? _ctrlTimer;
    private bool? _clickThrough; // last applied state (null = never applied)

    /// <summary>Starts/stops the Ctrl poll for the current mode and applies the matching click-through state.</summary>
    private void UpdateCtrlMode()
    {
        if (IsFloating)
        {
            if (_ctrlTimer == null)
            {
                _ctrlTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                _ctrlTimer.Tick += (_, _) => ApplyCtrlState();
            }
            _ctrlTimer.Start();
            ApplyCtrlState();
        }
        else
        {
            _ctrlTimer?.Stop();
            SetClickThrough(false);
            RootBorder.BorderBrush = null;
        }
    }

    private void ApplyCtrlState()
    {
        bool ctrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        bool interactive = ctrlDown || _rightDownCursor != null;
        SetClickThrough(!interactive);
        RootBorder.BorderBrush = interactive ? CtrlOutlineBrush : Brushes.Transparent;
    }

    private void SetClickThrough(bool on)
    {
        if (_clickThrough == on) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        style = on ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, style);
        _clickThrough = on;
    }

    private void PositionFloating()
    {
        var settings = SettingsStore.Instance;
        int x = settings.FloatingX, y = settings.FloatingY;

        // No saved position, or its monitor is gone → just above the docked position in the taskbar.
        bool valid = x != int.MinValue && y != int.MinValue &&
                     MonitorFromPoint(new POINT { X = x + 10, Y = y + 10 }, MONITOR_DEFAULTTONULL) != IntPtr.Zero;
        if (!valid)
        {
            var (_, taskbarRect) = GetTaskbarInfo();
            double s = GetMonitorScale();
            x = taskbarRect.Right - Math.Max(GetTrayWidth(), _maxTrayWidthPx) - (int)(Width * s);
            y = taskbarRect.Top - (int)(Height * s) - (int)(12 * s);
        }

        (x, y) = ClampToWorkArea(x, y);
        MoveTo(x, y);
    }

    /// <summary>
    /// Keeps the whole floating window inside the work area of its monitor (e.g. after it was
    /// scaled up near a screen edge). The saved position is left untouched, so scaling back
    /// down returns the window to where the user put it.
    /// </summary>
    private (int X, int Y) ClampToWorkArea(int x, int y)
    {
        var monitor = MonitorFromPoint(new POINT { X = x + 10, Y = y + 10 }, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref mi)) return (x, y);

        GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _);
        double s = dpiX / 96.0;
        int w = (int)Math.Ceiling(Width * s), h = (int)Math.Ceiling(Height * s);
        var work = mi.rcWork;
        x = Math.Max(work.Left, Math.Min(x, work.Right - w));
        y = Math.Max(work.Top, Math.Min(y, work.Bottom - h));
        return (x, y);
    }

    // ── Taskbar position ──────────────────────────────────────────────────────────

    private void PositionWindow()
    {
        if (_dragging) return;
        if (IsFloating) PositionFloating();
        else PositionDocked();
    }

    private void PositionDocked()
    {
        var (isBottom, taskbarRect) = GetTaskbarInfo();
        if (!isBottom) return; // skip non-bottom taskbars

        double scale = GetMonitorScale();
        int rawTrayWidthPx = GetTrayWidth();
        _lastTrayWidth = rawTrayWidthPx;
        // The tray width changes e.g. when the IME indicator switches (中/英/ENG). The widget only moves left
        // (when the tray grows), never right — otherwise it jumps around on every change.
        int trayWidthPx = Math.Max(rawTrayWidthPx, _maxTrayWidthPx);
        _maxTrayWidthPx = trayWidthPx;

        int widgetWidthPx = (int)(Width * scale);
        MoveTo(taskbarRect.Right - trayWidthPx - widgetWidthPx, taskbarRect.Top);
    }

    private void MoveTo(int xPx, int yPx)
    {
        var myHwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(myHwnd, IntPtr.Zero, xPx, yPx, 0, 0, SwpNoSize | SwpNoActivate | SwpNoZOrder);
        // WPF coordinates (for placing Settings)
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = xPx / dpi.DpiScaleX;
        Top = yPx / dpi.DpiScaleY;
    }

    private double GetMonitorScale()
    {
        var monitor = MonitorFromWindow(_taskbarHwnd, MONITOR_DEFAULTTONEAREST);
        GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _);
        return dpiX / 96.0;
    }

    private void SetTaskbarHeight()
    {
        if (!GetWindowRect(_taskbarHwnd, out RECT rect)) return;
        int heightPx = rect.Bottom - rect.Top;
        if (heightPx <= 0) return;
        _baseHeight = heightPx / GetMonitorScale();
        ApplyLayout();
    }

    private (bool isBottom, RECT taskbarRect) GetTaskbarInfo()
    {
        if (_isPrimary)
        {
            var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
            SHAppBarMessage(ABM_GETTASKBARPOS, ref data);
            return (data.uEdge == ABE_BOTTOM, data.rc);
        }

        GetWindowRect(_taskbarHwnd, out RECT rect);
        var monitor = MonitorFromWindow(_taskbarHwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref mi);
        bool isBottom = rect.Top > (mi.rcMonitor.Top + mi.rcMonitor.Bottom) / 2;
        return (isBottom, rect);
    }

    private int GetTrayWidth()
    {
        if (_isPrimary)
        {
            var hwndTray = FindWindowEx(_taskbarHwnd, IntPtr.Zero, "TrayNotifyWnd", null);
            if (hwndTray != IntPtr.Zero && GetWindowRect(hwndTray, out RECT trayRect))
                return trayRect.Right - trayRect.Left;
            return 200;
        }

        if (!GetWindowRect(_taskbarHwnd, out RECT taskbarRect)) return 150;
        int minLeft = taskbarRect.Right;
        EnumChildWindows(_taskbarHwnd, (child, _) =>
        {
            if (!IsWindowVisible(child)) return true;
            if (!GetWindowRect(child, out RECT cr)) return true;
            int childWidth = cr.Right - cr.Left;
            int taskbarWidth = taskbarRect.Right - taskbarRect.Left;
            if (cr.Left > (taskbarRect.Left + taskbarRect.Right) / 2 && childWidth < taskbarWidth / 2)
                if (cr.Left < minLeft) minLeft = cr.Left;
            return true;
        }, IntPtr.Zero);
        int trayWidth = taskbarRect.Right - minLeft;
        return trayWidth is > 20 and < 600 ? trayWidth : 120;
    }

    // ── Visibility (auto-hide taskbar, fullscreen apps) ───────────────────────────

    private void StartVisibilityTimer()
    {
        _visibilityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _visibilityTimer.Tick += (_, _) => CheckVisibility();
        _visibilityTimer.Start();
    }

    private async void CheckVisibility()
    {
        if (_visibilityCheckRunning) return;
        _visibilityCheckRunning = true;
        try
        {
            var (taskbarVisible, fullscreen) = await Task.Run(() =>
                (IsTaskbarVisible(), IsFullscreenOnMyMonitor()));

            // The floating window doesn't depend on the (auto-hide) taskbar; with a fullscreen app it hides just like the docked one.
            bool shouldShow = (taskbarVisible || IsFloating) && !fullscreen;

            if (fullscreen && !_fullscreenMode)
                EnterFullscreenMode();
            else if (!fullscreen && _fullscreenMode)
                ExitFullscreenMode();

            Visibility = shouldShow ? Visibility.Visible : Visibility.Hidden;
        }
        finally
        {
            _visibilityCheckRunning = false;
        }
    }

    private void EnterFullscreenMode()
    {
        _fullscreenMode = true;
        _refreshTimer?.Stop();
        _textTimer?.Stop();
        _trayWatchTimer?.Stop();
        _topMostEnforcer?.Pause();
        if (_visibilityTimer != null)
            _visibilityTimer.Interval = TimeSpan.FromSeconds(2);
    }

    private void ExitFullscreenMode()
    {
        _fullscreenMode = false;
        _refreshTimer?.Start();
        _textTimer?.Start();
        _trayWatchTimer?.Start();
        _topMostEnforcer?.Resume();
        if (_visibilityTimer != null)
            _visibilityTimer.Interval = TimeSpan.FromMilliseconds(500);
    }

    private bool IsTaskbarVisible()
    {
        if (_isPrimary)
        {
            var stateData = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
            uint state = SHAppBarMessage(ABM_GETSTATE, ref stateData);
            bool autoHideEnabled = (state & ABS_AUTOHIDE) != 0;
            if (!autoHideEnabled) return true;

            if (!GetWindowRect(_taskbarHwnd, out RECT actualRect)) return true;
            var posData = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
            SHAppBarMessage(ABM_GETTASKBARPOS, ref posData);
            return actualRect.Top <= posData.rc.Bottom;
        }

        if (!GetWindowRect(_taskbarHwnd, out RECT taskbarRect)) return true;
        var monitor = MonitorFromWindow(_taskbarHwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref mi)) return true;
        return taskbarRect.Top < mi.rcMonitor.Bottom - 2;
    }

    private bool IsFullscreenOnMyMonitor()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;

        // Desktop/shell windows cover full screen but aren't "fullscreen apps"
        var cls = new System.Text.StringBuilder(256);
        GetClassName(foreground, cls, 256);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "TaskListThumbnailWnd" or "MultitaskingViewFrame")
            return false;

        // Explorer.exe owns taskbar, thumbnail strips and Aero Peek overlays — never treat as fullscreen
        GetWindowThreadProcessId(foreground, out uint pid);
        if (!_isExplorerPidCache.TryGetValue(pid, out bool isExplorer))
        {
            try { isExplorer = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName
                      .Equals("explorer", StringComparison.OrdinalIgnoreCase); }
            catch { isExplorer = false; }
            _isExplorerPidCache[pid] = isExplorer;
        }
        if (isExplorer) return false;

        var myMonitor = MonitorFromWindow(_taskbarHwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(myMonitor, ref mi)) return false;

        var fgMonitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
        if (fgMonitor != myMonitor) return false;

        if (!GetWindowRect(foreground, out RECT fgRect)) return false;
        var mon = mi.rcMonitor;
        return fgRect.Left <= mon.Left && fgRect.Top <= mon.Top
            && fgRect.Right >= mon.Right && fgRect.Bottom >= mon.Bottom;
    }

#if DEBUG
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunRegistryValue = "ClaudeUsageWidget";

    private static void RemoveStartupIfThisExe()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: true);
        if (key == null) return;
        var value = key.GetValue(RunRegistryValue) as string;
        var thisExe = $"\"{Environment.ProcessPath}\"";
        if (string.Equals(value, thisExe, StringComparison.OrdinalIgnoreCase))
            key.DeleteValue(RunRegistryValue, throwOnMissingValue: false);
    }
#endif
}
