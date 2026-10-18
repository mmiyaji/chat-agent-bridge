using System.Globalization;
using System.Resources;

namespace ChatAgentBridge_Desktop.Localization;

internal static class ViewModelText
{
    private static readonly ResourceManager Resources = new(
        "ChatAgentBridge_Desktop.Localization.ViewModelStrings",
        typeof(ViewModelText).Assembly);

    public static string Get(string key, params object?[] args)
    {
        var template = Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;
        return args.Length == 0 ? template : string.Format(CultureInfo.CurrentCulture, template, args);
    }
}
