namespace ChatAgentBridge.Core.Appearance;

/// <summary>A traditional Japanese color usable as the app accent. Colors come from HiMemo's color themes.</summary>
/// <param name="Id">Stored identifier (HiMemo theme name).</param>
/// <param name="Label">Display name; the kanji with its reading, the same in every language.</param>
/// <param name="Light">Accent for light surfaces (HiMemo light-theme primary).</param>
/// <param name="Dark">Accent for dark surfaces (HiMemo dark-theme primary).</param>
/// <param name="IsBasic">Shown directly in the picker; the rest are under "more colors".</param>
public sealed record AccentColor(string Id, string Label, uint Light, uint Dark, bool IsBasic = false);

/// <summary>The seven accent shades WinUI reads (SystemAccentColor and its Light1-3 / Dark1-3 variants), as 0xRRGGBB.</summary>
public sealed record AccentRamp(uint Dark3, uint Dark2, uint Dark1, uint Accent, uint Light1, uint Light2, uint Light3);

public static class AccentPalette
{
    public const string System = "system";

    /// <summary>Basic colors first, then the rest in HiMemo's hue order.</summary>
    public static IReadOnlyList<AccentColor> Colors { get; } =
    [
        new("konjyo", "紺青 (Konjyo)", 0x113285, 0x9CB1FF, true),
        new("moegi", "萌黄 (Moegi)", 0x6F9335, 0xC3D88A, true),
        new("yamabuki", "山吹 (Yamabuki)", 0xB87900, 0xFFC85A, true),
        new("kurenai", "紅 (Kurenai)", 0xCB1B45, 0xFF9AAF, true),
        new("sakura", "桜 (Sakura)", 0xD98991, 0xFFB9BD, true),
        new("ai", "藍 (Ai)", 0x0F4C81, 0x85B4E6),
        new("ruri", "瑠璃 (Ruri)", 0x005CAF, 0x8FBFFF),
        new("hanada", "縹 (Hanada)", 0x006284, 0x7DC6E8),
        new("chigusa", "千草 (Chigusa)", 0x2E7898, 0x8ECEE8),
        new("asagi", "浅葱 (Asagi)", 0x23889A, 0x8BD9E5),
        new("sora", "空 (Sora)", 0x257EA8, 0xA4DFFF),
        new("tokiwa", "常磐 (Tokiwa)", 0x1B813E, 0x88D39B),
        new("seiheki", "青碧 (Seiheki)", 0x268785, 0x8FD4D1),
        new("wakatake", "若竹 (Wakatake)", 0x3F855F, 0x9BD8B6),
        new("byakuroku", "白緑 (Byakuroku)", 0x4F9C72, 0xC9EBD5),
        new("rikyucha", "利休茶 (Rikyucha)", 0x6F633D, 0xD2C48D),
        new("kurumi", "胡桃 (Kurumi)", 0x7A5B4C, 0xD6B8A8),
        new("nanohana", "菜の花 (Nanohana)", 0x9A7B00, 0xFFED76),
        new("enji", "臙脂 (Enji)", 0x9F353A, 0xFFA4A8),
        new("akane", "茜 (Akane)", 0xB7282E, 0xFFA2A5),
        new("haizakura", "灰桜 (Haizakura)", 0x9B7C71, 0xE2C9BE),
        new("sumi", "墨 (Sumi)", 0x1C1C1C, 0xBFC4C8),
        new("ginnezumi", "銀鼠 (Ginnezumi)", 0x70777E, 0xBCC5CC),
        new("shironeri", "白練 (Shironeri)", 0x6F6A60, 0xE7E5E0),
        new("gofun", "胡粉 (Gofun)", 0x8A6F2A, 0xFFEFC7),
        new("kikyo", "桔梗 (Kikyo)", 0x5B3F91, 0xC3B3F0),
        new("edomurasaki", "江戸紫 (Edomurasaki)", 0x77428D, 0xD1A8E0),
        new("sumire", "菫 (Sumire)", 0x66327C, 0xD5A8EB),
        new("fuji", "藤 (Fuji)", 0x6D63A8, 0xC9BFFF),
        new("shion", "紫苑 (Shion)", 0x6F5A96, 0xD2BDEE),
    ];

    public static bool IsSupported(string? id) => id == System || Find(id) is not null;

    public static AccentColor? Find(string? id) => Colors.FirstOrDefault(color => color.Id == id);

    // WinUI draws white text on the light-theme accent fill and black text on the dark-theme one.
    private const double MinimumTextContrast = 4.5;
    private const double Step = 0.08;

    /// <summary>
    /// Builds the shade ramp for one theme. Light theme fills use Dark1 and dark theme fills use Light2, so the HiMemo
    /// color sits there, adjusted only as far as needed for readable text on the fill.
    /// </summary>
    public static AccentRamp CreateRamp(AccentColor color, bool darkTheme)
    {
        var (h, s, l) = ToHsl(darkTheme ? color.Dark : color.Light);
        if (darkTheme)
        {
            while (Contrast(FromHsl(h, s, l), 0x000000) < MinimumTextContrast && l < 0.97) l = Math.Min(0.97, l + 0.01);
            return new(Shade(-4), Shade(-3), Shade(-2), Shade(-1), Shade(-0.5), Shade(0), Shade(0.75));
        }
        while (Contrast(FromHsl(h, s, l), 0xFFFFFF) < MinimumTextContrast && l > 0.03) l = Math.Max(0.03, l - 0.01);
        return new(Shade(-1.5), Shade(-0.75), Shade(0), Shade(1), Shade(2), Shade(3), Shade(4));

        uint Shade(double steps) => FromHsl(h, s, Math.Clamp(l + steps * Step, 0.02, 0.98));
    }

    /// <summary>WCAG contrast ratio between two 0xRRGGBB colors.</summary>
    public static double Contrast(uint a, uint b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(uint rgb)
    {
        static double Channel(uint value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(rgb >> 16 & 0xFF) + 0.7152 * Channel(rgb >> 8 & 0xFF) + 0.0722 * Channel(rgb & 0xFF);
    }

    private static (double H, double S, double L) ToHsl(uint rgb)
    {
        double r = (rgb >> 16 & 0xFF) / 255.0, g = (rgb >> 8 & 0xFF) / 255.0, b = (rgb & 0xFF) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        if (d == 0) return (0, 0, l);
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        var h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    private static uint FromHsl(double h, double s, double l)
    {
        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
        }
        double r, g, b;
        if (s == 0) r = g = b = l;
        else
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            (r, g, b) = (Hue(p, q, h + 1 / 3.0), Hue(p, q, h), Hue(p, q, h - 1 / 3.0));
        }
        static uint Byte(double v) => (uint)Math.Clamp(Math.Round(v * 255), 0, 255);
        return Byte(r) << 16 | Byte(g) << 8 | Byte(b);
    }
}
