// This probe is compiled out of production builds. It exercises the actual update
// preparation/abort path against the isolated offline chat fixture without restarting.
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    partial void InitializePortableUpdateRecoveryProbe();

#if CAB_PORTABLE_UPDATE_TEST
    partial void InitializePortableUpdateRecoveryProbe()
    {
        if (Environment.GetEnvironmentVariable("CAB_PORTABLE_UPDATE_ABORT_TEST") != "1" ||
            Environment.GetEnvironmentVariable("CAB_LIVE_STEERING_FIXTURE") != "1") return;

        var button = new Button { Content = "Run update recovery probe" };
        AutomationProperties.SetAutomationId(button, "PortableUpdateRecoveryProbe");
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                if (!await PrepareForPortableUpdateAsync())
                {
                    button.Content = "Update recovery probe blocked";
                    return;
                }
                await CancelPortableUpdatePreparationAsync();
                button.Content = "Update recovery probe completed";
            }
            catch { button.Content = "Update recovery probe failed"; }
            finally { button.IsEnabled = true; }
        };
        MainNavigation.FooterMenuItems.Insert(0, button);
    }
#endif
}
