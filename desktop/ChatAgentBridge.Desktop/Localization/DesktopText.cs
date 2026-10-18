using System.Globalization;
using System.Resources;

namespace ChatAgentBridge_Desktop.Localization;

/// <summary>Looks up desktop UI text using the active UI culture.</summary>
public static class DesktopText
{
    private static readonly ResourceManager Resources = new(
        "ChatAgentBridge_Desktop.Localization.DesktopStrings",
        typeof(DesktopText).Assembly);

    public static string Get(string key, params object?[] args)
    {
        var value = Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;
        return args.Length == 0 ? value : string.Format(CultureInfo.CurrentCulture, value, args);
    }
}
