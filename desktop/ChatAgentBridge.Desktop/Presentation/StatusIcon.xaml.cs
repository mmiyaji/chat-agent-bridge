using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop.Presentation;

public sealed partial class StatusIcon : UserControl
{
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(string), typeof(StatusIcon), new PropertyMetadata("neutral"));
    public string Tone { get => (string)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public StatusIcon() => InitializeComponent();
    public static Visibility Show(string tone, string expected) =>
        tone == expected ? Visibility.Visible : Visibility.Collapsed;
}
