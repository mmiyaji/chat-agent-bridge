using System.Globalization;
using System.Text.RegularExpressions;

namespace ChatAgentBridge.Core.Updates;

/// <summary>Ordering for the release tags used by the portable update feed.</summary>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch, bool IsPreview, int PreviewNumber) : IComparable<ReleaseVersion>
{
    private static readonly Regex Pattern = new(@"^v?(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-preview\.(?<preview>0|[1-9]\d*))?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool TryParse(string? value, out ReleaseVersion version)
    {
        version = default;
        if (value is null) return false;
        var match = Pattern.Match(value);
        if (!match.Success || !int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch)) return false;
        var preview = match.Groups["preview"];
        var parsedPreview = 0;
        if (preview.Success && !int.TryParse(preview.Value, NumberStyles.None, CultureInfo.InvariantCulture, out parsedPreview)) return false;
        var previewNumber = preview.Success ? parsedPreview : 0;
        version = new(major, minor, patch, preview.Success, previewNumber);
        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        if (result == 0) result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        if (IsPreview != other.IsPreview) return IsPreview ? -1 : 1;
        return IsPreview ? PreviewNumber.CompareTo(other.PreviewNumber) : 0;
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (IsPreview ? $"-preview.{PreviewNumber}" : string.Empty);
}
