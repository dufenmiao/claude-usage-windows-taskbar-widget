using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClaudeUsageWidgetProvider;

/// <summary>
/// Jedna dlaždice, která skládá limity více účtů (Codex + Claude) pod sebe:
/// každý řádek = ikona služby + barevná tečka, procento a odpočet do resetu.
/// </summary>
internal sealed class CombinedPanel : UserControl
{
    internal sealed record Row(ServiceType Service, RateLimitEntry? Limit, string? Error);

    private static readonly string[] SpinnerFrames = ["|", "/", "—", "\\"];
    private static readonly Dictionary<ServiceType, BitmapImage> IconCache = [];

    private readonly Grid _rowsGrid = new();
    private readonly List<TextBlock> _spinnerTexts = [];
    private int _spinnerFrame;

    public CombinedPanel()
    {
        Width = SettingsStore.DefaultTileWidth;
        Content = new Border { Padding = new Thickness(4, 3, 4, 3), Child = _rowsGrid };
    }

    public void SetTileWidth(double width) => Width = width;

    public void SetRows(IReadOnlyList<Row> rows)
    {
        _rowsGrid.Children.Clear();
        _rowsGrid.RowDefinitions.Clear();
        _spinnerTexts.Clear();

        double spacing = rows.Count <= 3 ? 2 : 1;
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0)
                _rowsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(spacing, GridUnitType.Pixel) });
            _rowsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var element = BuildRow(rows[i]);
            Grid.SetRow(element, i * 2);
            _rowsGrid.Children.Add(element);
        }
    }

    public void AdvanceSpinner()
    {
        if (_spinnerTexts.Count == 0) return;
        _spinnerFrame = (_spinnerFrame + 1) % SpinnerFrames.Length;
        foreach (var t in _spinnerTexts)
            t.Text = SpinnerFrames[_spinnerFrame];
    }

    private FrameworkElement BuildRow(Row row)
    {
        // [ikona] [barevná tečka] [procento] [odpočet do resetu]. Téměř průhledné pozadí (alfa 1), aby řádek
        // reagoval na myš i mimo text — plně průhledné pixely okna s AllowsTransparency propadají.
        var grid = new Grid { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new Image
        {
            Source = GetIcon(row.Service),
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var timeText = MakeText(Colors.White);
        var timeVb = WrapInShrinkingViewbox(timeText, HorizontalAlignment.Right);
        Grid.SetColumn(timeVb, 4);
        grid.Children.Add(timeVb);

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 6,
            Height = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        Grid.SetColumn(dot, 2);
        grid.Children.Add(dot);

        var pctText = MakeText(Colors.White);
        var pctVb = WrapInShrinkingViewbox(pctText, HorizontalAlignment.Right);
        Grid.SetColumn(pctVb, 3);
        grid.Children.Add(pctVb);

        if (row.Limit is { } limit)
        {
            dot.Fill = new SolidColorBrush(UsageColor(limit.Utilization));
            dot.Visibility = Visibility.Visible;
            pctText.Text = $"{limit.Utilization:0}%";
            timeText.Text = FormatCountdown(limit.ResetsAt);
        }
        else if (row.Error != null)
        {
            dot.Fill = new SolidColorBrush(UsageColor(100));
            dot.Visibility = Visibility.Visible;
            timeText.Text = "Error";
            timeText.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
            grid.ToolTip = row.Error;
        }
        else
        {
            pctText.Text = SpinnerFrames[_spinnerFrame];
            _spinnerTexts.Add(pctText);
        }

        return grid;
    }

    private static TextBlock MakeText(Color color) => new()
    {
        Foreground = new SolidColorBrush(color),
        FontSize = 9
    };

    /// <summary>Text v přirozené velikosti, který se jen zmenšuje (nikdy nezvětšuje), když se nevejde.</summary>
    private static Viewbox WrapInShrinkingViewbox(TextBlock text, HorizontalAlignment align) => new()
    {
        Stretch = Stretch.Uniform,
        StretchDirection = StretchDirection.DownOnly,
        HorizontalAlignment = align,
        VerticalAlignment = VerticalAlignment.Center,
        Child = text
    };

    /// <summary>
    /// Plynulý přechod barvy podle využití: 0 % zelená → 50 % žlutá → 100 % červená
    /// (odstín HSV 120° → 0°, plná sytost a jas).
    /// </summary>
    private static Color UsageColor(double utilization)
    {
        double t = Math.Clamp(utilization / 100.0, 0, 1);
        double hue = 120.0 * (1 - t);          // 120 = zelená, 0 = červená
        double x = 1 - Math.Abs(hue / 60.0 % 2 - 1);
        (double r, double g) = hue >= 60 ? (x, 1.0) : (1.0, x);
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), 0x30);
    }

    /// <summary>Stejné jako TimeFormatter.FormatResetTime, jen bez prefixu "in " ("5h 41m").</summary>
    private static string FormatCountdown(DateTimeOffset resetsAt)
    {
        var text = TimeFormatter.FormatResetTime(resetsAt);
        return text.StartsWith("in ", StringComparison.Ordinal) ? text[3..] : text;
    }

    private static BitmapImage GetIcon(ServiceType service)
    {
        if (IconCache.TryGetValue(service, out var cached)) return cached;
        var uri = service switch
        {
            ServiceType.Codex => "pack://application:,,,/Assets/codex-logo.png",
            _ => "pack://application:,,,/Assets/claude-logo.png"
        };
        var img = new BitmapImage(new Uri(uri));
        IconCache[service] = img;
        return img;
    }
}
