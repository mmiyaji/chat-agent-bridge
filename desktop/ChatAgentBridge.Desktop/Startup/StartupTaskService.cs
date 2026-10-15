using Windows.ApplicationModel;

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
                "Windows のスタートアップ設定を変更できませんでした。アプリをパッケージ版として起動しているか確認してください。");
        }
    }

    private static StartupTaskStatusResult Describe(Windows.ApplicationModel.StartupTaskState state) => state switch
    {
        Windows.ApplicationModel.StartupTaskState.Enabled => new(StartupState.Enabled, "Windows へのサインイン時に起動します。"),
        Windows.ApplicationModel.StartupTaskState.Disabled => new(StartupState.Disabled, "Windows へのサインイン時に起動しません。"),
        Windows.ApplicationModel.StartupTaskState.DisabledByUser => new(StartupState.DisabledByUser, "タスク マネージャーで無効にされています。再度有効にするには、タスク マネージャーの [スタートアップ アプリ] で有効にしてください。"),
        Windows.ApplicationModel.StartupTaskState.DisabledByPolicy => new(StartupState.DisabledByPolicy, "Windows のポリシーにより自動起動が無効です。"),
        _ => new(StartupState.Unavailable, "この環境では Windows の自動起動を利用できません。")
    };

    private static StartupTaskStatusResult Unavailable() => new(
        StartupState.Unavailable,
        "Windows のスタートアップ タスクを利用できません。パッケージ版アプリから設定してください。");
}
