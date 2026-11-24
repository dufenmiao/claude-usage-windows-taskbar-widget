using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace ClaudeUsageWidgetProvider;

internal class App : Application
{
    private Mutex? _mutex;

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("shell32.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string appId);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [STAThread]
    static void Main(string[] args)
    {
        WriteCrashLog("started");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog("[AppDomain] " + e.ExceptionObject?.ToString());

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("[UnobservedTask] " + e.Exception?.ToString());
            e.SetObserved();
        };

        var app = new App();
        app.DispatcherUnhandledException += (_, e) =>
        {
            WriteCrashLog("[Dispatcher] " + e.Exception?.ToString());
            e.Handled = true;
        };
        app.Run();
    }

    private static void WriteCrashLog(string? message)
    {
        try
        {
            var exePath = Environment.ProcessPath ?? "";
            var logPath = Path.ChangeExtension(exePath, ".log");
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]{Environment.NewLine}{message}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(logPath, entry);
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try { SetCurrentProcessExplicitAppUserModelId("ClaudeUsageWidget"); } catch { }
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\AppUserModelId\ClaudeUsageWidget");
            key.SetValue("DisplayName", "Claude Usage Widget");
        }
        catch { }

        _mutex = new Mutex(initiallyOwned: true, "Local\\ClaudeUsageWidget", out bool createdNew);
        if (!createdNew)
        {
            // A previous instance is running — kill it
            var currentId = Environment.ProcessId;
            var exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
            foreach (var p in Process.GetProcessesByName(exeName))
            {
                if (p.Id == currentId) continue;
                try { p.Kill(); p.WaitForExit(2000); } catch { }
            }
            // The previous instance is dead — acquire the mutex
            try { _mutex.WaitOne(3000); }
            catch (AbandonedMutexException) { /* ok — the previous one crashed, the mutex is ours */ }
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var clients = new List<ClaudeApiClient>();
        Exit += (_, _) =>
        {
            foreach (var c in clients) c.Dispose();
        };

        var primaryHwnd = FindWindow("Shell_TrayWnd", null);

        // SHOW the window IMMEDIATELY — without accounts. Credential loading calls wsl.exe (1-5s cold start);
        // loading synchronously would delay showing the widget by several seconds.
        var primaryWindow = new MainWindow(primaryHwnd, isPrimary: true);
        MainWindow = primaryWindow;
        primaryWindow.Show();

        var secondaryWindows = new List<MainWindow>();
        // PrimaryMonitorOnly: widget on the main taskbar only, skip secondary monitors.
        if (!SettingsStore.Instance.PrimaryMonitorOnly)
        EnumWindows((hwnd, _) =>
        {
            var sb = new StringBuilder(64);
            GetClassName(hwnd, sb, 64);
            if (sb.ToString() == "Shell_SecondaryTrayWnd")
            {
                var sw = new MainWindow(hwnd, isPrimary: false);
                sw.Show();
                secondaryWindows.Add(sw);
            }
            return true;
        }, IntPtr.Zero);

        // Load credentials asynchronously → push them to all windows
        _ = Task.Run(async () =>
        {
            List<AccountInfo> accounts;
            try
            {
                accounts = CredentialStore.LoadAllAccounts();
                WriteCrashLog($"LoadAllAccounts: {accounts.Count} accounts ({string.Join(", ", accounts.Select(a => $"{a.Service}/{a.Label}"))})");
            }
            catch (Exception ex)
            {
                WriteCrashLog("LoadAllAccounts FAILED: " + ex);
                accounts = [];
            }
            if (accounts.Count == 0)
                accounts.Add(new AccountInfo(ServiceType.Claude, new OAuthCredential(), "no-credentials"));

            await Dispatcher.InvokeAsync(() =>
            {
                clients.AddRange(accounts.Select(a => new ClaudeApiClient(a)));

                _ = primaryWindow.AddAccountsAsync(clients);
                foreach (var sw in secondaryWindows)
                    _ = sw.AddAccountsAsync(clients);
            });
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

internal static class TimeFormatter
{
    internal static string FormatResetTime(DateTimeOffset resetTime)
    {
        var remaining = resetTime - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return "now";
        if (remaining.TotalHours >= 1)
            return $"in {(int)remaining.TotalHours}h {remaining.Minutes}m";
        return $"in {remaining.Minutes}m";
    }
}
