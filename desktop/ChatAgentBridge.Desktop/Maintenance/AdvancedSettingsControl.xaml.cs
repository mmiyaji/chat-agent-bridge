using System.Globalization;
using ChatAgentBridge.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.Maintenance;

public sealed partial class AdvancedSettingsControl : UserControl
{
    public Func<LogRetentionPreferences, Task>? ApplyRequested
    {
        get => LogManagementControl.ApplyRequested;
        set => LogManagementControl.ApplyRequested = value;
    }

    public AdvancedSettingsControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LanguageSettings.LanguageChanged -= LanguageChanged;
        LanguageSettings.LanguageChanged += LanguageChanged;
        Localize();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => LanguageSettings.LanguageChanged -= LanguageChanged;

    private void LanguageChanged(CultureInfo culture) => DispatcherQueue.TryEnqueue(Localize);

    private void Localize()
    {
        AdvancedHeading.Text = DesktopText.Get("AdvancedSettingsHeader");
        AdvancedDescription.Text = DesktopText.Get("AdvancedSettingsDescription");
    }
}
