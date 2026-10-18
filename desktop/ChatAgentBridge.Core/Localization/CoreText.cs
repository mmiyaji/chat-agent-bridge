using System.Globalization;
using System.Resources;

namespace ChatAgentBridge.Core.Localization;

public static class CoreText
{
    private static readonly ResourceManager Resources = new("ChatAgentBridge.Core.Localization.Strings", typeof(CoreText).Assembly);

    public static string Get(string key, params object?[] args)
    {
        var value = Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;
        return args.Length == 0 ? value : string.Format(CultureInfo.CurrentCulture, value, args);
    }
}
