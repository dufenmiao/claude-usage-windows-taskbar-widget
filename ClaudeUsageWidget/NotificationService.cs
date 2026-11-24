namespace ClaudeUsageWidgetProvider;

internal sealed class NotificationService
{
    public static readonly NotificationService Instance = new();

    private static readonly double[] Thresholds = [75, 90];

    // key = "{accountKey}:{limitLabel}" → set of thresholds already notified
    private readonly Dictionary<string, HashSet<double>> _notified = new();

    // key = "{accountKey}:{limitLabel}" → last known ResetsAt value
    private readonly Dictionary<string, DateTimeOffset> _lastResetAt = new();

    // key = "{accountKey}:{limitLabel}" → last known utilization value
    private readonly Dictionary<string, double> _lastUtilization = new();

    private NotificationService() { }

    public void CheckAndNotify(string? accountKey, UsageData usage)
    {
        if (string.IsNullOrEmpty(accountKey)) return;
        var settings = SettingsStore.Instance;

        foreach (var limit in usage.Limits)
        {
            var key = $"{accountKey}:{limit.Label}";
            _notified.TryGetValue(key, out var notifiedSet);
            notifiedSet ??= new HashSet<double>();
            _notified[key] = notifiedSet;

            // Detect reset: ResetsAt changed → clear notified + optionally toast
            _lastUtilization.TryGetValue(key, out var prevUtil);
            if (_lastResetAt.TryGetValue(key, out var prevReset) && limit.ResetsAt != prevReset)
            {
                notifiedSet.Clear();
                if (settings.NotificationsEnabled && settings.NotifyOnReset && limit.Utilization < 10 && prevUtil > 0)
                    ShowToast($"Limit reset: {FormatLabel(limit.Label)}", "Usage has been reset.");
            }
            _lastResetAt[key] = limit.ResetsAt;
            _lastUtilization[key] = limit.Utilization;

            // Remove thresholds if utilization dropped below them
            foreach (var threshold in Thresholds)
            {
                if (limit.Utilization < threshold)
                    notifiedSet.Remove(threshold);
            }

            // Notify if utilization crossed a threshold
            if (!settings.NotificationsEnabled) continue;
            foreach (var threshold in Thresholds)
            {
                if (limit.Utilization >= threshold && notifiedSet.Add(threshold))
                {
                    var severity = threshold >= 90 ? "Critical" : "Warning";
                    ShowToast(
                        $"{severity}: {FormatLabel(limit.Label)} at {limit.Utilization:0}%",
                        $"Reset {TimeFormatter.FormatResetTime(limit.ResetsAt)}");
                }
            }
        }
    }

    /// <summary>
    /// Notification via a balloon tip on a temporary tray icon — Windows 10/11 shows it as a regular toast.
    /// (Previously WinRT ToastNotification, but that pulled 25 MB of Windows SDK projection into the build.)
    /// The icon is removed once the balloon closes/expires. Called on the UI thread.
    /// </summary>
    private static void ShowToast(string title, string body)
    {
        try
        {
            var icon = new System.Windows.Forms.NotifyIcon
            {
                Icon = AppIcon,
                Text = "Claude Usage Widget",
                Visible = true
            };

            bool disposed = false;
            void Cleanup()
            {
                if (disposed) return;
                disposed = true;
                icon.Visible = false;
                icon.Dispose();
            }

            icon.BalloonTipClosed += (_, _) => Cleanup();
            icon.BalloonTipClicked += (_, _) => Cleanup();
            icon.ShowBalloonTip(10_000, title, body, System.Windows.Forms.ToolTipIcon.Warning);

            // Safety net: if Windows drops the balloon without an event, remove the icon after a while anyway.
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            timer.Tick += (_, _) => { timer.Stop(); Cleanup(); };
            timer.Start();
        }
        catch { }
    }

    private static System.Drawing.Icon? _appIcon;
    private static System.Drawing.Icon AppIcon =>
        _appIcon ??= (Environment.ProcessPath is { } exe ? System.Drawing.Icon.ExtractAssociatedIcon(exe) : null)
                     ?? System.Drawing.SystemIcons.Information;

    private static string FormatLabel(string apiLabel)
    {
        var parts = apiLabel.Split('-');
        return parts.Length >= 2 ? parts[^1].ToUpperInvariant() : apiLabel.ToUpperInvariant();
    }
}
