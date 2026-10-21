using Microsoft.UI.Xaml;

namespace ChatAgentBridge_Desktop.Localization;

/// <summary>Preserves an element's x:Uid for in-process language refresh.</summary>
public static class LocalizedResourceKey
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(LocalizedResourceKey), new PropertyMetadata(null));

    public static string? GetKey(DependencyObject element) => (string?)element.GetValue(KeyProperty);

    public static void SetKey(DependencyObject element, string? value) => element.SetValue(KeyProperty, value);
}
