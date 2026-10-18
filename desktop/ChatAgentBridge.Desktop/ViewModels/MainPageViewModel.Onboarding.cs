using ChatAgentBridge.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    private readonly OnboardingProgressStore _onboardingProgressStore = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartOnboardingCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousOnboardingCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextOnboardingCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeferOnboardingCommand))]
    public partial bool IsOnboardingBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnboardingTitle))]
    [NotifyPropertyChangedFor(nameof(OnboardingDescription))]
    [NotifyPropertyChangedFor(nameof(OnboardingNextLabel))]
    [NotifyPropertyChangedFor(nameof(OnboardingProgress))]
    [NotifyCanExecuteChangedFor(nameof(PreviousOnboardingCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextOnboardingCommand))]
    public partial int OnboardingStep { get; set; }

    [ObservableProperty] public partial bool IsOnboardingActive { get; set; }

    public string OnboardingTitle => OnboardingStep switch
    {
        0 => "エージェントと作業フォルダー",
        1 => "チャット接続と許可ユーザー",
        _ => "接続して動作を確認"
    };

    public string OnboardingDescription => OnboardingStep switch
    {
        0 => "エージェントの実行ファイルと既存の作業フォルダーを設定します。CLIへのログインは別途必要です。",
        1 => "SlackまたはMattermostの接続情報と許可するユーザーIDを設定します。トークンはチャットに入力しないでください。",
        _ => "チャット受信を開始し、接続を確認します。承認方法の初期値は依頼ごとに確認です。"
    };

    public string OnboardingNextLabel => OnboardingStep switch
    {
        0 => "保存して接続設定へ",
        1 => "保存して接続確認へ",
        _ => "完了"
    };

    public string OnboardingProgress => $"{Math.Clamp(OnboardingStep, 0, 2) + 1} / 3";

    public event Action<int>? OnboardingNavigateRequested;

    public async Task InitializeOnboardingAsync()
    {
        if (!IsSettingsLoaded) return;
        try
        {
            var progress = await _onboardingProgressStore.LoadAsync();
            if (!OnboardingProgressStore.ShouldStart(
                    File.Exists(Path.Combine(_settingsStore.DirectoryPath, "settings.json")), progress))
                return;

            OnboardingStep = Math.Clamp(progress?.Step ?? 0, 0, 2);
            if (progress is null) await SaveOnboardingProgressAsync("active", OnboardingStep);
            IsOnboardingActive = true;
            OnboardingNavigateRequested?.Invoke(OnboardingStep);
        }
        catch (Exception ex)
        {
            SetStatus("セットアップ案内を読み込めませんでした", ex.Message, "Error");
        }
    }

    [RelayCommand(CanExecute = nameof(CanOnboardingAction))]
    private async Task StartOnboardingAsync()
    {
        await RunOnboardingActionAsync(async () =>
        {
            OnboardingStep = 0;
            await SaveOnboardingProgressAsync("active", 0);
            IsOnboardingActive = true;
            OnboardingNavigateRequested?.Invoke(0);
        });
    }

    [RelayCommand(CanExecute = nameof(CanGoBackInOnboarding))]
    private async Task PreviousOnboardingAsync()
    {
        if (OnboardingStep <= 0) return;
        await RunOnboardingActionAsync(async () =>
        {
            var step = OnboardingStep - 1;
            await SaveOnboardingProgressAsync("active", step);
            OnboardingStep = step;
            OnboardingNavigateRequested?.Invoke(step);
        });
    }

    [RelayCommand(CanExecute = nameof(CanOnboardingAction))]
    private async Task NextOnboardingAsync()
    {
        await RunOnboardingActionAsync(async () =>
        {
            if (OnboardingStep == 0)
            {
                if (string.IsNullOrWhiteSpace(WorkspaceRoot) || !Path.IsPathFullyQualified(WorkspaceRoot) || !Directory.Exists(WorkspaceRoot))
                {
                    SetStatus("作業フォルダーを確認してください", "存在するフォルダーの絶対パスを指定してください。", "Warning");
                    return;
                }
                if (string.IsNullOrWhiteSpace(AgentExecutable))
                {
                    SetStatus("エージェントを設定してください", "エージェントの実行ファイルを指定してください。", "Warning");
                    return;
                }
                if (!await SaveSettingsForOnboardingAsync()) return;
                await AdvanceOnboardingAsync(1);
                return;
            }

            if (OnboardingStep == 1)
            {
                if (!await SaveSettingsForOnboardingAsync()) return;
                if (!HasUsableSavedChatConfiguration())
                {
                    SetStatus("チャット接続を確認してください", "SlackはBot・Appトークンと許可ユーザーID、MattermostはサーバーURL・チャンネルID・トークンと許可ユーザーIDが必要です。", "Warning");
                    return;
                }
                await AdvanceOnboardingAsync(2);
                return;
            }

            if (!_chatConnectionConfirmed)
            {
                SetStatus("チャット接続を確認してください", "保存した接続先でチャット受信を開始してください。", "Warning");
                return;
            }
            if (_settings is null || _settings != CreateSettingsSnapshot())
            {
                SetStatus("設定を保存してください", "現在の設定に未保存の変更があります。保存してから完了してください。", "Warning");
                return;
            }
            await SaveOnboardingProgressAsync("completed", 2);
            IsOnboardingActive = false;
            SetStatus("セットアップが完了しました", "チャットから依頼できます。", "Success");
        });
    }

    [RelayCommand(CanExecute = nameof(CanOnboardingAction))]
    private async Task DeferOnboardingAsync()
    {
        await RunOnboardingActionAsync(async () =>
        {
            await SaveOnboardingProgressAsync("deferred", OnboardingStep);
            IsOnboardingActive = false;
        });
    }

    private bool CanOnboardingAction() => IsSettingsLoaded && !IsLoading && !IsRunning && !IsOnboardingBusy;
    private bool CanGoBackInOnboarding() => CanOnboardingAction() && OnboardingStep > 0;

    private async Task<bool> SaveSettingsForOnboardingAsync()
    {
        await SaveSettingsAsync();
        return _lastSaveSucceeded;
    }

    private bool HasUsableSavedChatConfiguration()
    {
        if (_settings is null) return false;
        var slackReady = !string.IsNullOrWhiteSpace(_settings.SlackBotToken) &&
                         !string.IsNullOrWhiteSpace(_settings.SlackAppToken) &&
                         HasAllowedUsers(_settings.SlackAllowedUserIds);
        var mattermostReady = !string.IsNullOrWhiteSpace(_settings.MattermostServerUrl) &&
                              !string.IsNullOrWhiteSpace(_settings.MattermostChannelId) &&
                              !string.IsNullOrWhiteSpace(_settings.MattermostToken) &&
                              HasAllowedUsers(_settings.MattermostAllowedUserIds);
        return slackReady || mattermostReady;
    }

    private async Task AdvanceOnboardingAsync(int step)
    {
        await SaveOnboardingProgressAsync("active", step);
        OnboardingStep = step;
        OnboardingNavigateRequested?.Invoke(step);
    }

    private async Task SaveOnboardingProgressAsync(string status, int step)
    {
        try
        {
            await _onboardingProgressStore.SaveAsync(new OnboardingProgress(Math.Clamp(step, 0, 2), status));
        }
        catch (Exception ex)
        {
            SetStatus("セットアップの進行状況を保存できませんでした", ex.Message, "Error");
            throw;
        }
    }

    private async Task RunOnboardingActionAsync(Func<Task> action)
    {
        if (!CanOnboardingAction()) return;
        IsOnboardingBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            SetStatus("セットアップを保存できませんでした", ex.Message, "Error");
        }
        finally
        {
            IsOnboardingBusy = false;
        }
    }
}
