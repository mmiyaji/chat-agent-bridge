namespace ChatAgentBridge_Desktop.Presentation;

internal static class UserIdentityText
{
    public static string Format(string? name, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return string.IsNullOrWhiteSpace(name) ? "—" : name;
        return string.IsNullOrWhiteSpace(name) || name == id ? id : $"{name} ({id})";
    }
}
