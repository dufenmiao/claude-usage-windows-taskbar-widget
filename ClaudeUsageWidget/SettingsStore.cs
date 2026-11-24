using Microsoft.Win32;

namespace ClaudeUsageWidgetProvider;

internal sealed class SettingsStore
{
    public static readonly SettingsStore Instance = new();

    private const string RegistryPath = @"Software\ClaudeUsageWidget";
    private const string HiddenLimitsValueName = "HiddenLimits"; // comma-separated label list

    public bool NotificationsEnabled { get; set; }
    public bool NotifyOnReset { get; set; }

    public bool ShowClaude { get; set; } = true;
    public bool ShowCodex { get; set; } = true;
    // Show the widget on the primary monitor only (applies after an app restart).
    public bool PrimaryMonitorOnly { get; set; } = true;

    public const double DefaultTileWidth = 120;
    public double TileWidth { get; set; } = DefaultTileWidth;

    // Floating mode: widget can be dragged anywhere on screen (position in physical px).
    public bool FloatingMode { get; set; }
    public int FloatingX { get; set; } = int.MinValue;
    public int FloatingY { get; set; } = int.MinValue;
    // Opacity in floating mode (0–100 %): tile background and the whole window.
    public int FloatingBackgroundOpacity { get; set; } = 90;
    public int FloatingWindowOpacity { get; set; } = 100;

    // Labels of rows the user has hidden (e.g. "session-5h")
    public HashSet<string> HiddenLimits { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public static event Action? VisibilityChanged;
    public static void RaiseVisibilityChanged() => VisibilityChanged?.Invoke();

    public static event Action? KnownLabelsChanged;

    // Limit labels seen at runtime (label → "Claude / unified-7d") — for the toggles in Settings.
    private readonly Dictionary<string, string> _knownLabels = new(StringComparer.OrdinalIgnoreCase);

    public bool IsLimitHidden(string label) => HiddenLimits.Contains(label);

    public void RegisterKnownLabels(ServiceType service, UsageData usage)
    {
        var prefix = service == ServiceType.Codex ? "Codex" : "Claude";
        var changed = false;
        foreach (var limit in usage.Limits)
        {
            var display = $"{prefix} / {limit.Label}";
            if (_knownLabels.TryGetValue(limit.Label, out var current) && current == display)
                continue;
            _knownLabels[limit.Label] = display;
            changed = true;
        }

        if (changed)
            KnownLabelsChanged?.Invoke();
    }

    public IReadOnlyList<(string Label, string Display)> GetKnownLabels() =>
        _knownLabels.Select(kv => (kv.Key, kv.Value)).OrderBy(x => x.Value).ToList();

    private SettingsStore()
    {
        Load();
    }

    private void Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            if (key == null) return;
            NotificationsEnabled = ReadBool(key, "NotificationsEnabled", false);
            NotifyOnReset = ReadBool(key, "NotifyOnReset", false);
            ShowClaude = ReadBool(key, "ShowClaude", true);
            ShowCodex = ReadBool(key, "ShowCodex", true);
            PrimaryMonitorOnly = ReadBool(key, "PrimaryMonitorOnly", true);
            // TileWidth replaced ClaudeWidth/CodexWidth — carry over the older setting.
            TileWidth = ParseWidth(key.GetValue("TileWidth") ?? key.GetValue("ClaudeWidth"), DefaultTileWidth);
            FloatingMode = ReadBool(key, "FloatingMode", false);
            FloatingX = (int)(key.GetValue("FloatingX", int.MinValue) ?? int.MinValue);
            FloatingY = (int)(key.GetValue("FloatingY", int.MinValue) ?? int.MinValue);
            FloatingBackgroundOpacity = Math.Clamp((int)(key.GetValue("FloatingBackgroundOpacity", 90) ?? 90), 0, 100);
            FloatingWindowOpacity = Math.Clamp((int)(key.GetValue("FloatingWindowOpacity", 100) ?? 100), 20, 100);
            var hiddenRaw = key.GetValue(HiddenLimitsValueName) as string;
            if (!string.IsNullOrEmpty(hiddenRaw))
                foreach (var lbl in hiddenRaw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    HiddenLimits.Add(lbl.Trim());
        }
        catch { }
    }

    public void Save()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key.SetValue("NotificationsEnabled", NotificationsEnabled ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("NotifyOnReset", NotifyOnReset ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("ShowClaude", ShowClaude ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("ShowCodex", ShowCodex ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("PrimaryMonitorOnly", PrimaryMonitorOnly ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("TileWidth", TileWidth.ToString(System.Globalization.CultureInfo.InvariantCulture), RegistryValueKind.String);
            key.SetValue("FloatingMode", FloatingMode ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("FloatingX", FloatingX, RegistryValueKind.DWord);
            key.SetValue("FloatingY", FloatingY, RegistryValueKind.DWord);
            key.SetValue("FloatingBackgroundOpacity", FloatingBackgroundOpacity, RegistryValueKind.DWord);
            key.SetValue("FloatingWindowOpacity", FloatingWindowOpacity, RegistryValueKind.DWord);
            key.SetValue(HiddenLimitsValueName, string.Join(",", HiddenLimits), RegistryValueKind.String);
        }
        catch { }
    }

    private static bool ReadBool(RegistryKey key, string name, bool fallback) =>
        (int)(key.GetValue(name, fallback ? 1 : 0) ?? (fallback ? 1 : 0)) != 0;

    private static double ParseWidth(object? raw, double fallback)
    {
        if (raw is string s &&
            double.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var w) && w >= 50 && w <= 600)
            return w;
        return fallback;
    }
}
