using Windows.ApplicationModel;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.Startup;

public enum StartupState
{
    Enabled,
    Disabled,
    DisabledByUser,
    DisabledByPolicy,
    Unavailable,
    Error
}

public sealed record StartupTaskStatusResult(StartupState State, string Message)
{
    public bool IsEnabled => State == StartupState.Enabled;
}

/// <summary>Manages the app's manifest-declared, user-controlled Windows login task.</summary>
public sealed class StartupTaskService
{
    public const string TaskId = "ChatAgentBridgeAutoStart";

    public async Task<StartupTaskStatusResult> GetStateAsync()
    {
        try
        {
            StartupTask task = await StartupTask.GetAsync(TaskId);
            return Describe(task.State);
        }
        catch (Exception)
        {
            return Unavailable();
        }
    }

    public async Task<StartupTaskStatusResult> SetEnabledAsync(bool enabled)
    {
        try
        {
            StartupTask task = await StartupTask.GetAsync(TaskId);
            if (enabled)
            {
                if (task.State == Windows.ApplicationModel.StartupTaskState.DisabledByUser)
                    return Describe(task.State);
                if (task.State == Windows.ApplicationModel.StartupTaskState.DisabledByPolicy)
                    return Describe(task.State);

                Windows.ApplicationModel.StartupTaskState state = await task.RequestEnableAsync();
                return Describe(state);
            }

            task.Disable();
            return Describe(task.State);
        }
        catch (Exception)
        {
            return new StartupTaskStatusResult(
                StartupState.Error,
                DesktopText.Get("StartupChangeError"));
        }
    }

    private static StartupTaskStatusResult Describe(Windows.ApplicationModel.StartupTaskState state) => state switch
    {
        Windows.ApplicationModel.StartupTaskState.Enabled => new(StartupState.Enabled, DesktopText.Get("StartupEnabled")),
        Windows.ApplicationModel.StartupTaskState.Disabled => new(StartupState.Disabled, DesktopText.Get("StartupDisabled")),
        Windows.ApplicationModel.StartupTaskState.DisabledByUser => new(StartupState.DisabledByUser, DesktopText.Get("StartupDisabledByUser")),
        Windows.ApplicationModel.StartupTaskState.DisabledByPolicy => new(StartupState.DisabledByPolicy, DesktopText.Get("StartupDisabledByPolicy")),
        _ => new(StartupState.Unavailable, DesktopText.Get("StartupUnavailable"))
    };

    private static StartupTaskStatusResult Unavailable() => new(
        StartupState.Unavailable,
        DesktopText.Get("StartupTaskUnavailable"));
}
