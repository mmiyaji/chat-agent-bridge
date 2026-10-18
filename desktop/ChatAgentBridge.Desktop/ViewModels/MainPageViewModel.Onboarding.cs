using ChatAgentBridge.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    private readonly OnboardingProgressStore _onboardingProgressStore = new();
    private readonly OnboardingConsentStore _onboardingConsentStore = new();
    private bool _hasCurrentConsent;
    [ObservableProperty] public partial bool HasAcceptedTerms { get; set; }
    [ObservableProperty] public partial bool HasAcceptedPrivacy { get; set; }
    [ObservableProperty] public partial string ConsentStatus { get; set; } = "文書を開いて内容をご確認ください。同意しない場合は、後で設定するかアプリを終了できます。";
    private BridgeSettings? _wizardConnectedSettings;
    private string? _wizardConnectedProvider;
    public void ClearWizardConnection() { _wizardConnectedSettings = null; _wizardConnectedProvider = null; }
    public void RememberWizardConnectionSettings(string provider)
    {
        _wizardConnectedSettings = _settings;
        _wizardConnectedProvider = provider;
    }
    partial void OnIsOnboardingBusyChanged(bool value) => CheckCliCommand.NotifyCanExecuteChanged();
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartOnboardingCommand)), NotifyCanExecuteChangedFor(nameof(PreviousOnboardingCommand)), NotifyCanExecuteChangedFor(nameof(NextOnboardingCommand)), NotifyCanExecuteChangedFor(nameof(DeferOnboardingCommand))]
    public partial bool IsOnboardingBusy { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OnboardingTitle)), NotifyPropertyChangedFor(nameof(OnboardingDescription)), NotifyPropertyChangedFor(nameof(OnboardingNextLabel)), NotifyPropertyChangedFor(nameof(OnboardingProgress)), NotifyPropertyChangedFor(nameof(IsWizardIntro)), NotifyPropertyChangedFor(nameof(IsWizardAgentStep)), NotifyPropertyChangedFor(nameof(IsWizardCliStep)), NotifyPropertyChangedFor(nameof(IsWizardChatStep)), NotifyPropertyChangedFor(nameof(IsWizardSafetyStep)), NotifyCanExecuteChangedFor(nameof(PreviousOnboardingCommand))]
    public partial int OnboardingStep { get; set; }
    [ObservableProperty] public partial bool IsOnboardingActive { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWizardSlack)), NotifyPropertyChangedFor(nameof(IsWizardMattermost)), NotifyPropertyChangedFor(nameof(WizardSummary)), NotifyPropertyChangedFor(nameof(WizardAutoConnect))]
    public partial string OnboardingChatProvider { get; set; } = "slack";
    public bool WizardAutoConnect
    {
        get => AutoConnectProvider == OnboardingChatProvider;
        set { AutoConnectProvider = value ? OnboardingChatProvider : "none"; OnPropertyChanged(); }
    }
    public bool IsWizardIntro => OnboardingStep == 0;
    public bool IsWizardAgentStep => OnboardingStep == 1;
    public bool IsWizardCliStep => OnboardingStep == 2;
    public bool IsWizardChatStep => OnboardingStep == 3;
    public bool IsWizardSafetyStep => OnboardingStep == 4;
    public bool IsWizardSlack => OnboardingChatProvider == "slack";
    public bool IsWizardMattermost => OnboardingChatProvider == "mattermost";
    public string OnboardingTitle => $"{OnboardingProgress} " + (OnboardingStep switch
    { 0 => "はじめに・利用条件", 1 => "AIと作業フォルダー", 2 => "CLIの確認とモデル", 3 => "チャットの接続情報", _ => "承認設定と接続確認" });
    public string OnboardingDescription => OnboardingStep switch
    {
        0 => "仕組みと利用条件を確認してから、このPCの初期設定へ進みます。",
        1 => "このPCで使うAIと、作業するフォルダーを選びます。CLIのインストールとログインは事前に必要です。",
        2 => "チェックを実行し、CLIの種類・バージョン・モデル候補を確認します。AIへ作業の依頼は送りません。",
        3 => "SlackまたはMattermostを選び、接続情報と利用を許可する本人のユーザーIDを入力します。",
        _ => "依頼の承認と結果の共有方法を確認し、選んだチャットに接続して完了します。"
    };
    public string OnboardingNextLabel => OnboardingStep == 0 ? "同意して次へ" : OnboardingStep == 4 ? "設定を保存して完了" : "保存して次へ";
    public string OnboardingProgress => $"[{Math.Clamp(OnboardingStep, 0, 4) + 1} / 5]";
    public string WizardSummary => $"AI: {(AgentProvider == "claude" ? "Claude Code" : "Codex")} / モデル: {(string.IsNullOrWhiteSpace(AgentModel) ? "CLIの既定値" : AgentModel)}\n推論の強さ: {(string.IsNullOrWhiteSpace(AgentReasoningEffort) ? "CLIの既定値" : AgentReasoningEffort)}\n作業フォルダー: {WorkspaceRoot}\nチャット: {(IsWizardSlack ? "Slack" : "Mattermost")} / アプリ起動時の接続: {(WizardAutoConnect ? "有効" : "無効")}\n依頼の承認: {ChatApprovalLabel(ChatApprovalMode)} / 結果: {ResultLabel(ChatResultMode)}\nAIの操作権限: {PermissionLabel(AgentPermissionMode)}";
    public event Action<int>? OnboardingNavigateRequested;

    public async Task InitializeOnboardingAsync()
    {
        if (!IsSettingsLoaded) return;
        try
        {
            var progress = await _onboardingProgressStore.LoadAsync();
            if (!OnboardingProgressStore.ShouldStart(File.Exists(Path.Combine(_settingsStore.DirectoryPath, "settings.json")), progress)) return;
            _hasCurrentConsent = OnboardingConsentStore.IsCurrent(await _onboardingConsentStore.LoadAsync());
            HasAcceptedTerms = HasAcceptedPrivacy = _hasCurrentConsent;
            OnboardingStep = !_hasCurrentConsent ? 0 : progress?.Step > 1 ? 2 : progress?.Step ?? 0;
            OnboardingChatProvider = PreferredWizardProvider();
            await SaveOnboardingProgressAsync("active", OnboardingStep);
            IsOnboardingActive = true;
            IsStatusOpen = false;
            OnboardingNavigateRequested?.Invoke(OnboardingStep);
        }
        catch { SetStatus("初期設定の進行状況を読み込めませんでした", "保存済みの案内データを確認してください。設定は上書きしていません。", "Error"); }
    }
    private string PreferredWizardProvider() => AutoConnectProvider is "slack" or "mattermost" ? AutoConnectProvider : !string.IsNullOrWhiteSpace(MattermostToken) && string.IsNullOrWhiteSpace(SlackBotToken) ? "mattermost" : "slack";
    [RelayCommand(CanExecute = nameof(CanOnboardingAction))]
    private Task StartOnboardingAsync() => RunOnboardingActionAsync(async () =>
    {
        OnboardingChatProvider = PreferredWizardProvider();
        HasAcceptedTerms = HasAcceptedPrivacy = false;
        ConsentStatus = "文書を開いて内容をご確認ください。このガイドでは両方に同意してから進みます。";
        await SaveOnboardingProgressAsync("active", 0);
        OnboardingStep = 0;
        IsOnboardingActive = true;
        IsStatusOpen = false;
        OnboardingNavigateRequested?.Invoke(0);
    });
    [RelayCommand(CanExecute = nameof(CanGoBackInOnboarding))]
    private Task PreviousOnboardingAsync() => RunOnboardingActionAsync(() => AdvanceOnboardingAsync(OnboardingStep - 1));
    [RelayCommand(CanExecute = nameof(CanOnboardingAction))]
    private Task NextOnboardingAsync() => RunOnboardingActionAsync(async () =>
    {
        IsStatusOpen = false;
        if (OnboardingStep == 0)
        {
            if (!HasAcceptedTerms || !HasAcceptedPrivacy) { SetStatus("利用条件への同意が必要です", "利用規約とプライバシーポリシーの両方を確認し、同意する場合はチェックを入れてください。", "Warning"); return; }
            await _onboardingConsentStore.SaveAsync(new OnboardingConsent(OnboardingConsentStore.CurrentPolicyVersion, DateTimeOffset.UtcNow));
            _hasCurrentConsent = true;
            ConsentStatus = $"文書バージョン {OnboardingConsentStore.CurrentPolicyVersion} への同意をこのPCに保存しました。";
            await AdvanceOnboardingAsync(1);
            return;
        }
        if (!_hasCurrentConsent || !HasAcceptedTerms || !HasAcceptedPrivacy) { SetStatus("利用条件を確認してください", "最初のページへ戻り、利用規約とプライバシーポリシーをご確認ください。", "Warning"); return; }
        if (OnboardingStep == 1)
        {
            if (AgentProvider is not ("codex" or "claude")) { SetStatus("AIを選択してください", "このウィザードではCodexまたはClaude Codeを設定できます。", "Warning"); return; }
            if (!Path.IsPathFullyQualified(WorkspaceRoot) || !Directory.Exists(WorkspaceRoot)) { SetStatus("作業フォルダーを確認してください", "存在するフォルダーの絶対パスを入力するか、「フォルダーを選択」で指定してください。", "Warning"); return; }
        }
        if (OnboardingStep == 2 && !HasCurrentCliCheck()) { SetStatus("CLIとモデルを確認してください", CliCheckSucceeded ? "入力したモデルはCLIの候補にありません。候補から選択するか、モデル名を空欄にしてCLIの既定値を使ってください。" : "「CLIをチェック」でバージョンとモデル候補を確認してください。失敗した場合はCLIの場所・ログイン状態を確認して再実行できます。", "Warning"); return; }
        if (OnboardingStep == 3 && !HasChosenChatConfiguration()) { SetStatus("接続情報を確認してください", IsWizardSlack ? "Botトークン・Appトークン・許可するユーザーIDを入力してください。" : "サーバーURL・チャンネルID・トークン・許可するユーザーIDを入力してください。", "Warning"); return; }
        if (OnboardingStep == 4)
        {
            if (!HasCurrentCliCheck()) { SetStatus("CLIを再確認してください", "AIまたは作業フォルダーの設定が変わりました。ステップ3へ戻ってチェックしてください。", "Warning"); return; }
            if (!HasChosenChatConfiguration()) { SetStatus("接続情報を確認してください", "選択したチャットの接続情報と許可するユーザーIDが必要です。", "Warning"); return; }
            if (!_chatConnectionConfirmed) { SetStatus("チャットへの接続が必要です", "この画面の接続ボタンを押し、「接続しました」と表示されてから完了してください。", "Warning"); return; }
            if (_wizardConnectedProvider != OnboardingChatProvider || _wizardConnectedSettings != CreateSettingsSnapshot()) { SetStatus("変更した設定で再接続してください", "この画面の接続ボタンを押すと、現在の設定を保存して再接続します。", "Warning"); return; }
            if (_settings != CreateSettingsSnapshot()) { await SaveSettingsAsync(); if (_lastSaveSucceeded) SetStatus("設定を保存しました", "承認設定などの変更を反映するため、一度切断してこの画面から再接続してください。", "Warning"); return; }
        }
        await SaveSettingsAsync();
        if (!_lastSaveSucceeded) return;
        if (OnboardingStep < 4) { await AdvanceOnboardingAsync(OnboardingStep + 1); return; }
        await SaveOnboardingProgressAsync("completed", 4);
        IsOnboardingActive = false;
        SetStatus("初期設定が完了しました", "チャットから依頼できます。CLIチェックではAIの作業実行は行っていません。", "Success");
    });
    public async Task<bool> SaveWizardConnectionSettingsAsync()
    {
        if (!_hasCurrentConsent || !HasAcceptedTerms || !HasAcceptedPrivacy || !HasCurrentCliCheck() || !HasChosenChatConfiguration()) { SetStatus("設定内容を確認してください", "CLIチェックと選択したチャットの接続情報が必要です。", "Warning"); return false; }
        await SaveSettingsAsync();
        return _lastSaveSucceeded;
    }
    [RelayCommand(CanExecute = nameof(CanOnboardingAction))]
    private Task DeferOnboardingAsync() => RunOnboardingActionAsync(async () =>
    {
        CancelCliCheck();
        await SaveOnboardingProgressAsync("deferred", OnboardingStep);
        IsOnboardingActive = false;
    });
    private bool CanOnboardingAction() => IsSettingsLoaded && !IsLoading && !IsRunning && !IsOnboardingBusy && !IsCheckingCli;
    private bool CanGoBackInOnboarding() => CanOnboardingAction() && OnboardingStep > 0;
    private bool HasChosenChatConfiguration() => IsWizardSlack
        ? !string.IsNullOrWhiteSpace(SlackBotToken) && !string.IsNullOrWhiteSpace(SlackAppToken) && HasAllowedUsers(SlackAllowedUserIds)
        : IsWizardMattermost && IsWizardMattermostUrlValid() && !string.IsNullOrWhiteSpace(MattermostChannelId) && !string.IsNullOrWhiteSpace(MattermostToken) && HasAllowedUsers(MattermostAllowedUserIds);
    private bool IsWizardMattermostUrlValid() => Uri.TryCreate(MattermostServerUrl.Trim(), UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    private async Task AdvanceOnboardingAsync(int step)
    {
        await SaveOnboardingProgressAsync("active", step);
        OnboardingStep = step;
        IsStatusOpen = false;
        OnboardingNavigateRequested?.Invoke(step);
    }
    private Task SaveOnboardingProgressAsync(string status, int step) => _onboardingProgressStore.SaveAsync(new OnboardingProgress(step, status));
    private async Task RunOnboardingActionAsync(Func<Task> action)
    {
        if (!CanOnboardingAction()) return;
        IsOnboardingBusy = true;
        try { await action(); }
        catch { SetStatus("初期設定を保存できませんでした", "入力内容と設定フォルダーへの書き込み権限を確認してください。", "Error"); }
        finally { IsOnboardingBusy = false; }
    }
}
