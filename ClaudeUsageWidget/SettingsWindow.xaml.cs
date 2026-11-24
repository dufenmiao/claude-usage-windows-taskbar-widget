using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ClaudeUsageWidgetProvider;

public partial class SettingsWindow : Window
{
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunRegistryValue = "ClaudeUsageWidget";
    private bool _closing;

    /// <summary>Transparent margin around the window for the shadow (Grid Margin in XAML) — MainWindow subtracts it when aligning.</summary>
    internal const double ShadowMargin = 14;
    private readonly Dictionary<string, CheckBox> _limitShowChecks = new();

    public SettingsWindow()
    {
        InitializeComponent();
        Icon = new System.Windows.Media.Imaging.BitmapImage(
            new Uri("pack://application:,,,/Assets/claude-logo.png"));
        VersionText.Text = $"v{AppVersion.Current}";

        var settings = SettingsStore.Instance;
        ShowClaudeCheck.IsChecked = settings.ShowClaude;
        ShowCodexCheck.IsChecked = settings.ShowCodex;
        PrimaryMonitorOnlyCheck.IsChecked = settings.PrimaryMonitorOnly;
        FloatingModeCheck.IsChecked = settings.FloatingMode;
        BgOpacitySlider.Value = settings.FloatingBackgroundOpacity;
        WindowOpacitySlider.Value = settings.FloatingWindowOpacity;
        BgOpacityText.Text = $"{settings.FloatingBackgroundOpacity}%";
        WindowOpacityText.Text = $"{settings.FloatingWindowOpacity}%";
        BgOpacitySlider.ValueChanged += (_, _) => SaveOpacity();
        WindowOpacitySlider.ValueChanged += (_, _) => SaveOpacity();
        UpdateFloatingOptionsState();
        FloatingModeCheck.Checked += (_, _) => UpdateFloatingOptionsState();
        FloatingModeCheck.Unchecked += (_, _) => UpdateFloatingOptionsState();
        TileWidthBox.Text = settings.TileWidth.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        NotificationsCheck.IsChecked = settings.NotificationsEnabled;
        NotifyResetCheck.IsChecked = settings.NotifyOnReset;
        StartupCheck.IsChecked = IsStartupEnabled();

#if DEBUG
        StartupCheck.IsEnabled = false;
#endif

        BuildLimitsUI(settings);
        SettingsStore.KnownLabelsChanged += OnKnownLabelsChanged;
        Closed += (_, _) => SettingsStore.KnownLabelsChanged -= OnKnownLabelsChanged;

        TileWidthBox.TextChanged += (_, _) => SaveVisibility();
        foreach (var check in new[] { ShowClaudeCheck, ShowCodexCheck, PrimaryMonitorOnlyCheck, FloatingModeCheck })
        {
            check.Checked += (_, _) => SaveVisibility();
            check.Unchecked += (_, _) => SaveVisibility();
        }
        NotificationsCheck.Checked += (_, _) => SaveSettings();
        NotificationsCheck.Unchecked += (_, _) => SaveSettings();
        NotifyResetCheck.Checked += (_, _) => SaveSettings();
        NotifyResetCheck.Unchecked += (_, _) => SaveSettings();
        StartupCheck.Checked += (_, _) => SetStartup(true);
        StartupCheck.Unchecked += (_, _) => SetStartup(false);

        CloseButton.Click += (_, _) => SafeClose();
        QuitButton.Click += (_, _) => Application.Current.Shutdown();
    }

    private void SaveVisibility()
    {
        var settings = SettingsStore.Instance;
        settings.ShowClaude = ShowClaudeCheck.IsChecked == true;
        settings.ShowCodex = ShowCodexCheck.IsChecked == true;
        settings.PrimaryMonitorOnly = PrimaryMonitorOnlyCheck.IsChecked == true;
        settings.FloatingMode = FloatingModeCheck.IsChecked == true;
        settings.TileWidth = ParseWidthOrDefault(TileWidthBox.Text, settings.TileWidth);
        settings.Save();
        SettingsStore.RaiseVisibilityChanged();
    }

    /// <summary>Opacity sliders only matter in floating mode — otherwise dimmed and disabled.</summary>
    private void UpdateFloatingOptionsState()
    {
        bool on = FloatingModeCheck.IsChecked == true;
        FloatingOptions.IsEnabled = on;
        FloatingOptions.Opacity = on ? 1.0 : 0.4;
    }

    /// <summary>"Claude / unified-7d" → "Claude · 7d", "Codex / session-5h" → "Codex · 5h".</summary>
    private static string PrettyLimitName(string display)
    {
        var parts = display.Split(" / ", 2);
        if (parts.Length != 2) return display;
        var window = parts[1];
        foreach (var prefix in new[] { "unified-", "session-" })
            if (window.StartsWith(prefix, StringComparison.Ordinal))
                window = window[prefix.Length..];
        return $"{parts[0]} · {window}";
    }

    private void SaveOpacity()
    {
        var settings = SettingsStore.Instance;
        settings.FloatingBackgroundOpacity = (int)BgOpacitySlider.Value;
        settings.FloatingWindowOpacity = (int)WindowOpacitySlider.Value;
        BgOpacityText.Text = $"{settings.FloatingBackgroundOpacity}%";
        WindowOpacityText.Text = $"{settings.FloatingWindowOpacity}%";
        settings.Save();
        SettingsStore.RaiseVisibilityChanged();
    }

    private static double ParseWidthOrDefault(string raw, double fallback)
    {
        if (double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var w) && w >= 50 && w <= 600)
            return w;
        return fallback;
    }

    private void OnKnownLabelsChanged() => Dispatcher.Invoke(() =>
    {
        LimitsPanel.Children.Clear();
        _limitShowChecks.Clear();
        BuildLimitsUI(SettingsStore.Instance);
    });

    /// <summary>One toggle per known limit (e.g. "Codex 5h") — hides/shows its row.</summary>
    private void BuildLimitsUI(SettingsStore settings)
    {
        var labels = settings.GetKnownLabels();
        LimitsCard.Visibility = labels.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (labels.Count == 0) return;

        var toggleStyle = (Style)FindResource("ToggleRow");
        foreach (var (label, display) in labels)
        {
            var check = new CheckBox
            {
                Content = PrettyLimitName(display),
                ToolTip = label,
                IsChecked = !settings.IsLimitHidden(label),
                Style = toggleStyle
            };
            check.Checked += (_, _) => SaveSettings();
            check.Unchecked += (_, _) => SaveSettings();
            LimitsPanel.Children.Add(check);
            _limitShowChecks[label] = check;
        }
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void SaveSettings()
    {
        var settings = SettingsStore.Instance;
        settings.NotificationsEnabled = NotificationsCheck.IsChecked == true;
        settings.NotifyOnReset = NotifyResetCheck.IsChecked == true;

        foreach (var (label, check) in _limitShowChecks)
        {
            if (check.IsChecked == true) settings.HiddenLimits.Remove(label);
            else settings.HiddenLimits.Add(label);
        }

        settings.Save();
        // HiddenLimits changed → MainWindow must redraw the rows
        SettingsStore.RaiseVisibilityChanged();
    }

    private static bool IsStartupEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunRegistryKey);
        return key?.GetValue(RunRegistryValue) != null;
    }

    private static void SetStartup(bool enable)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: true);
        if (key == null) return;
        if (enable)
            key.SetValue(RunRegistryValue, $"\"{System.Environment.ProcessPath}\"");
        else
            key.DeleteValue(RunRegistryValue, throwOnMissingValue: false);
    }
}
