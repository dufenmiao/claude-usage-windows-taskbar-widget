using System.Reflection;

namespace ClaudeUsageWidgetProvider;

internal static class AppVersion
{
    /// <summary>"0.1.0" → "0.1"; the patch number is shown only when non-zero.</summary>
    public static string Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v == null) return "0.0";
            return v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";
        }
    }
}
