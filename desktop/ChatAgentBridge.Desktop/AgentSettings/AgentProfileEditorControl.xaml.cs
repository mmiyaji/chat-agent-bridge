using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.Localization;
using ChatAgentBridge_Desktop.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using System.ComponentModel;
using System.Globalization;

namespace ChatAgentBridge_Desktop.AgentSettings;

public sealed partial class AgentProfileEditorControl : UserControl, INotifyPropertyChanged
{
    private MainPageViewModel? _shared;
    private Action? _reloadProcessEnvironment;
    private DispatcherQueueTimer? _saveTimer;
    private Task<bool>? _flushTask;
    private bool _isBusy;
    private bool _isConfigured;
    private bool _isShuttingDown;
    private bool _isFlushing;
    private bool _isPickingExecutable;
    private bool _isActivatingProfile;
    private bool _sharedContextMismatch;
    private bool _showDraftSaveError;
    private string _currentProvider = "";
    private string? _lastSharedWorkspace;
    private string? _lastSharedTrustedRoots;
    private Task<bool>? _contextRefreshTask;
    private bool _editorEventsAttached;
    private bool _isTextFieldFocused;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? OpenCommonSettings;

    public MainPageViewModel Editor { get; } = new(isAgentProfileEditor: true);
    public bool HasPendingEdits => Editor.HasUnsavedSettings;
    public string CheckStatusLabel => MrtText.Get(Editor.IsCheckingCli ? "AgentEditorChecking" : Editor.CliCheckSucceeded ? "AgentEditorChecked" : Editor.CliCheckFailed ? "AgentEditorCheckFailed" : "AgentEditorNotChecked");
    // One icon slot for every state keeps the status text from shifting when the check starts or finishes.
    public string CheckStatusGlyph => Editor.CliCheckSucceeded ? "" : Editor.CliCheckFailed ? "" : "";
    public Microsoft.UI.Xaml.Media.Brush CheckStatusBrush => (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
        Editor.CliCheckSucceeded ? "SystemFillColorSuccessBrush" : Editor.CliCheckFailed ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush"];
    public static Visibility InverseVisibility(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    // The model list keeps its place while the check refreshes it; it is only disabled until choices arrive.
    public static bool HasModelChoices(int count, bool loading) => count > 0 && !loading;
    public string CurrentProvider => _currentProvider;
    public bool CanChangeSelection => _isConfigured && !_isBusy && !_isShuttingDown && !Editor.IsCheckingCli &&
                                      !Editor.IsLoading && !Editor.ModelCatalog.IsLoading;
    public bool CanRunCliCheck => _isConfigured && !_isShuttingDown && !_isPickingExecutable && !_isActivatingProfile &&
                                  !_sharedContextMismatch && !Editor.IsCheckingCli && (!Editor.IsLoading || _isFlushing) && !Editor.IsRunning &&
                                  !Editor.ModelCatalog.IsLoading && (!_isBusy || _isFlushing);
    public bool CanUseAgentProfile => CanChangeSelection && Editor.HasVerifiedAgentEditor &&
                                      !_sharedContextMismatch && (HasUnappliedChanges || !IsActiveProvider);
    public bool IsActiveProvider => _shared?.PersistedSettings is { } saved &&
                                    string.Equals(saved.AgentProvider, Editor.AgentProvider, StringComparison.Ordinal);
    public bool HasUnappliedChanges
    {
        get
        {
            if (_shared?.PersistedSettings is not { } saved || !Editor.IsSettingsLoaded) return false;
            // A checked executable may be resolved from "codex" to its full path.
            // Compare the canonical profile that Use applies, rather than treating
            // that harmless spelling difference as a change still awaiting use.
            if (Editor.GetVerifiedAgentEditorProfile() is { } verified)
            {
                var applied = verified.ApplyTo(saved);
                return applied != saved;
            }
            return !string.Equals(saved.AgentProvider, Editor.AgentProvider, StringComparison.Ordinal) ||
                   !string.Equals(saved.AgentExecutable, Editor.AgentExecutable, StringComparison.Ordinal) ||
                   !string.Equals(saved.AgentArguments, Editor.AgentArguments, StringComparison.Ordinal) ||
                   !string.Equals(saved.AgentMode, Editor.UseAppServer ? "app-server" : "exec", StringComparison.Ordinal) ||
                   !string.Equals(saved.AgentModel ?? "", Editor.AgentModel ?? "", StringComparison.Ordinal) ||
                   !string.Equals(saved.AgentReasoningEffort ?? "", Editor.AgentReasoningEffort ?? "", StringComparison.Ordinal) ||
                   !string.Equals(saved.AgentPermissionMode, Editor.AgentPermissionMode, StringComparison.Ordinal) ||
                   saved.AllowAgentNetwork != Editor.AllowAgentNetwork;
        }
    }
    public string PendingChangesLabel => MrtText.Get("AgentProfilePendingChanges");
    public Visibility ArgumentsVisibility => Editor.IsArgumentsEditable ? Visibility.Visible : Visibility.Collapsed;

    public AgentProfileEditorControl()
    {
        InitializeComponent();
        Loaded += Control_Loaded;
        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += SaveTimer_Tick;
        Unloaded += Control_Unloaded;
        ApplyResponsiveLayout();
    }

    public void Configure(MainPageViewModel shared, Func<BridgeSettings, BridgeSettings, Task<bool>> confirm,
        Action reloadEnvironment)
    {
        ArgumentNullException.ThrowIfNull(shared);
        if (_shared is not null && !ReferenceEquals(_shared, shared))
        {
            _shared.PropertyChanged -= Shared_PropertyChanged;
            _shared.SettingsSaved -= Shared_SettingsSaved;
        }
        _shared = shared;
        _lastSharedWorkspace = shared.PersistedSettings?.WorkspaceRoot;
        _lastSharedTrustedRoots = shared.PersistedSettings?.TrustedWorkspaceRoots;
        shared.PropertyChanged -= Shared_PropertyChanged;
        shared.PropertyChanged += Shared_PropertyChanged;
        shared.SettingsSaved -= Shared_SettingsSaved;
        shared.SettingsSaved += Shared_SettingsSaved;
        _reloadProcessEnvironment = reloadEnvironment;
        Editor.ConfirmSensitiveSettingsAsync = confirm;
        _isConfigured = true;
        NotifyStateChanged();
    }

    public async Task<bool> SelectProviderAsync(string provider)
    {
        if (_isShuttingDown || !_isConfigured || _shared?.PersistedSettings is not { } settings) return false;
        if (string.Equals(provider, _currentProvider, StringComparison.Ordinal) && Editor.IsSettingsLoaded && !_sharedContextMismatch) return true;
        if ((_currentProvider.Length > 0 && !CanChangeSelection) || !await TryFlushAsync()) return false;

        SetBusy(true);
        try
        {
            await Editor.LoadAgentEditorAsync(settings, provider);
            if (!Editor.IsSettingsLoaded) return false;
            _currentProvider = Editor.AgentProvider;
            _sharedContextMismatch = false;
            _lastSharedWorkspace = settings.WorkspaceRoot;
            _lastSharedTrustedRoots = settings.TrustedWorkspaceRoots;
            OnPropertyChanged(nameof(CurrentProvider));
            NotifyStateChanged();
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    public async Task<bool> TryFlushAsync()
    {
        if (!_isConfigured || !Editor.IsSettingsLoaded) return true;
        if (_flushTask is { IsCompleted: false } pending) return await pending;
        if (Editor.IsCheckingCli || Editor.ModelCatalog.IsLoading) return false;
        _saveTimer?.Stop();
        _flushTask = FlushPendingDraftAsync();
        return await _flushTask;
    }

    public Task<bool> RefreshSharedContextAsync()
    {
        if (_contextRefreshTask is { IsCompleted: false } pending) return pending;
        _contextRefreshTask = RefreshSharedContextCoreAsync();
        return _contextRefreshTask;
    }

    private async Task<bool> RefreshSharedContextCoreAsync()
    {
        if (_shared is null || _currentProvider.Length == 0 || _shared.PersistedSettings is not { } settings) return true;
        if (!_sharedContextMismatch && string.Equals(_lastSharedWorkspace, settings.WorkspaceRoot, StringComparison.Ordinal) &&
            string.Equals(_lastSharedTrustedRoots, settings.TrustedWorkspaceRoots, StringComparison.Ordinal)) return true;

        if (Editor.IsCheckingCli) Editor.CancelCliCheckCommand.Execute(null);
        var checkTask = Editor.CheckCliCommand.ExecutionTask;
        if (checkTask is not null)
        {
            try { await checkTask; } catch { }
        }
        if (Editor.ModelCatalog.IsLoading) Editor.ModelCatalog.CancelLookup();
        if (!await TryFlushAsync()) return false;

        SetBusy(true);
        try
        {
            await Editor.LoadAgentEditorAsync(settings, _currentProvider);
            if (!Editor.IsSettingsLoaded) return false;
            _sharedContextMismatch = false;
            _lastSharedWorkspace = settings.WorkspaceRoot;
            _lastSharedTrustedRoots = settings.TrustedWorkspaceRoots;
            NotifyStateChanged();
            return true;
        }
        finally { SetBusy(false); }
    }

    public void OpenSection(string? section)
    {
        AdvancedExpander.IsExpanded = true;
        if (string.Equals(section, "model", StringComparison.OrdinalIgnoreCase))
            AgentModelControl.Focus(FocusState.Programmatic);
    }

    public void RefreshLocalizedText()
    {
        Editor.RefreshLocalizedText();
        MrtText.Refresh(this, CultureInfo.CurrentUICulture);
        OnPropertyChanged(nameof(PendingChangesLabel));
        NotifyStateChanged();
    }

    public async Task ShutdownAsync()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;
        if (_shared is not null)
        {
            _shared.PropertyChanged -= Shared_PropertyChanged;
            _shared.SettingsSaved -= Shared_SettingsSaved;
        }
        DetachEditorEvents();
        _saveTimer?.Stop();
        Editor.ModelCatalog.CancelLookup();
        if (Editor.IsCheckingCli) Editor.CancelCliCheckCommand.Execute(null);
        var checkTask = Editor.CheckCliCommand.ExecutionTask;
        if (checkTask is not null)
        {
            try { await checkTask; } catch { }
        }
        if (_flushTask is { IsCompleted: false } flushTask)
        {
            try { await flushTask; } catch { }
        }
        SetBusy(false);
        NotifyStateChanged();
    }

    private async Task<bool> FlushPendingDraftAsync()
    {
        if (!Editor.HasUnsavedSettings)
        {
            _showDraftSaveError = false;
            NotifyStateChanged();
            return true;
        }

        _isFlushing = true;
        SetBusy(true);
        try
        {
            var saved = await Editor.SavePendingSettingsAsync(retrySensitiveConfirmation: true);
            _showDraftSaveError = !saved && Editor.HasUnsavedSettings;
            NotifyStateChanged();
            return saved;
        }
        catch
        {
            _showDraftSaveError = true;
            NotifyStateChanged();
            return false;
        }
        finally
        {
            _isFlushing = false;
            SetBusy(false);
        }
    }

    private void QueueDraftSave()
    {
        if (!_isConfigured || _isShuttingDown || Editor.IsLoading) return;
        _showDraftSaveError = false;
        _saveTimer?.Stop();
        _saveTimer?.Start();
        NotifyStateChanged();
    }

    private async void SaveTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_isTextFieldFocused)
        {
            sender.Start();
            return;
        }
        await TryFlushAsync();
    }

    private void EditorLayout_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout();

    private void ApplyResponsiveLayout()
    {
        if (WorkspaceGrid is null) return;
        var compact = EditorLayout.ActualWidth < 600;
        ApplyResponsiveRow(WorkspaceGrid, WorkspaceLabel, WorkspaceValue, OpenCommonSettingsButton, compact, 16);
        ApplyResponsiveRow(ExecutableGrid, ExecutableLabel, ExecutableInput, BrowseAgentExecutableButton, compact, 10);
        ApplyResponsiveRow(ModelGrid, ModelLabel, AgentModelControl, FetchModelsButton, compact, 10);
        ApplyResponsiveRow(ReasoningGrid, ReasoningLabel, ReasoningCombo, null, compact, 10);
        ApplyResponsiveRow(PermissionGrid, PermissionLabel, PermissionCombo, null, compact, 10);
        ApplyResponsiveRow(ArgumentsGrid, ArgumentsLabel, ArgumentsInput, null, compact, 10);
        CheckCliActions.Margin = compact ? new Thickness(0) : new Thickness(200, 0, 0, 0);
    }

    private static void ApplyResponsiveRow(Grid grid, FrameworkElement label, FrameworkElement field,
        FrameworkElement? action, bool compact, double spacing)
    {
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        grid.ColumnSpacing = spacing;
        if (compact)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (action is not null)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(label, 0);
            Grid.SetColumn(label, 0);
            Grid.SetColumnSpan(label, action is null ? 1 : 2);
            Grid.SetRow(field, 1);
            Grid.SetColumn(field, 0);
            Grid.SetColumnSpan(field, 1);
            if (action is not null)
            {
                Grid.SetRow(action, 1);
                Grid.SetColumn(action, 1);
                Grid.SetColumnSpan(action, 1);
            }
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (action is not null)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(label, 0);
            Grid.SetColumn(label, 0);
            Grid.SetColumnSpan(label, 1);
            Grid.SetRow(field, 0);
            Grid.SetColumn(field, 1);
            Grid.SetColumnSpan(field, 1);
            if (action is not null)
            {
                Grid.SetRow(action, 0);
                Grid.SetColumn(action, 2);
                Grid.SetColumnSpan(action, 1);
            }
        }
    }

    private async void CheckCli_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRunCliCheck) return;
        if (_flushTask is { IsCompleted: false } pendingFlush && !await pendingFlush) return;
        if (Editor.HasUnsavedSettings && !await TryFlushAsync()) return;
        if (!CanRunCliCheck) return;
        SetBusy(true);
        try
        {
            await Editor.CheckCliCommand.ExecuteAsync(null);
            if (Editor.CliCheckSucceeded)
                await Editor.CommitAgentEditorCheckAsync();
            if (_shared is not null)
                await _shared.SynchronizeAgentEditorCliVerificationAsync(Editor);
        }
        catch
        {
            // The editor VM owns the localized verification message and draft state.
        }
        finally
        {
            SetBusy(false);
            NotifyStateChanged();
        }
    }

    private async void BrowseAgentExecutable_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (_flushTask is { IsCompleted: false } pendingFlush && !await pendingFlush) return;
        if (!CanChangeSelection) return;
        button.IsEnabled = false;
        _isPickingExecutable = true;
        SetBusy(true);
        try
        {
            var picker = new FileOpenPicker(button.XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                CommitButtonText = DesktopText.Get("ChooseExecutable")
            };
            ChatAgentBridge_Desktop.MainPage.ConfigureExecutablePicker(picker, Editor.AgentProvider);
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                Editor.AgentExecutable = file.Path;
                await TryFlushAsync();
            }
        }
        catch
        {
            _showDraftSaveError = true;
            NotifyStateChanged();
        }
        finally
        {
            _isPickingExecutable = false;
            SetBusy(false);
            button.IsEnabled = true;
        }
    }

    private async void UseAgentProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!CanUseAgentProfile || _shared is null) return;
        _isActivatingProfile = true;
        SetBusy(true);
        try
        {
            if (Editor.HasUnsavedSettings && !await Editor.CommitAgentEditorCheckAsync()) return;
            if (Editor.AgentProvider == "custom")
                await _shared.ActivateCheckedCustomAgentEditorAsync(Editor);
            else if (Editor.GetVerifiedAgentEditorProfile() is { } profile)
                await _shared.ActivateAgentEditorAsync(profile, Editor);
            NotifyStateChanged();
        }
        finally
        {
            _isActivatingProfile = false;
            SetBusy(false);
        }
    }

    private void OpenCommonSettings_Click(object sender, RoutedEventArgs e) => OpenCommonSettings?.Invoke();
    private void ReloadProcessEnvironment_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChangeSelection) return;
        _reloadProcessEnvironment?.Invoke();
        Editor.OnProcessEnvironmentReloaded();
        NotifyStateChanged();
    }
    private async void RetryDraftSave_Click(object sender, RoutedEventArgs e) => await TryFlushAsync();

    private void DraftField_GotFocus(object sender, RoutedEventArgs e) => _isTextFieldFocused = true;

    private async void DraftField_LostFocus(object sender, RoutedEventArgs e)
    {
        _isTextFieldFocused = false;
        await TryFlushAsync();
    }

    private void DraftSelection_Changed(object sender, RoutedEventArgs e) => QueueDraftSave();
    private void Model_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) QueueDraftSave();
    }

    private void Editor_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainPageViewModel.AgentExecutable) or nameof(MainPageViewModel.AgentArguments) or
            nameof(MainPageViewModel.AgentModel) or nameof(MainPageViewModel.AgentReasoningEffort) or
            nameof(MainPageViewModel.AgentPermissionMode) or nameof(MainPageViewModel.AllowAgentNetwork) or nameof(MainPageViewModel.UseAppServer))
        {
            QueueDraftSave();
            NotifyStateChanged();
        }
        else if (e.PropertyName is nameof(MainPageViewModel.IsCheckingCli) or nameof(MainPageViewModel.IsLoading) or
                 nameof(MainPageViewModel.IsSettingsLoaded) or nameof(MainPageViewModel.HasUnsavedSettings) or
                 nameof(MainPageViewModel.CliCheckSucceeded) or nameof(MainPageViewModel.CliCheckFailed) or
                 nameof(MainPageViewModel.CliVersion) or nameof(MainPageViewModel.CliCheckSummary) or
                 nameof(MainPageViewModel.AgentProvider) or nameof(MainPageViewModel.AgentProviderDisplayName) or
                 nameof(MainPageViewModel.SupportsModel) or nameof(MainPageViewModel.SupportsReasoning) or nameof(MainPageViewModel.SupportsModelCatalog) or nameof(MainPageViewModel.CanSelectReadOnlyPermission) or
                 nameof(MainPageViewModel.IsArgumentsEditable) or nameof(MainPageViewModel.CanConfigureAgentNetwork) or
                 nameof(MainPageViewModel.CanConfigureAgentPermission) or nameof(MainPageViewModel.AgentPermissionStatus) or
                 nameof(MainPageViewModel.HasVerifiedAgentEditor) or
                 nameof(MainPageViewModel.AgentExecutable) or nameof(MainPageViewModel.AgentArguments) or
                 nameof(MainPageViewModel.AgentModel) or nameof(MainPageViewModel.AgentReasoningEffort) or
                 nameof(MainPageViewModel.AgentPermissionMode) or nameof(MainPageViewModel.AllowAgentNetwork) or nameof(MainPageViewModel.UseAppServer))
            NotifyStateChanged();
    }

    private void Shared_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainPageViewModel.PersistedSettings)) CheckSharedContext();
    }

    private void Shared_SettingsSaved() => CheckSharedContext();

    private void CheckSharedContext()
    {
        if (_shared?.PersistedSettings is not { } saved) return;
        var changed = !string.Equals(_lastSharedWorkspace, saved.WorkspaceRoot, StringComparison.Ordinal) ||
                      !string.Equals(_lastSharedTrustedRoots, saved.TrustedWorkspaceRoots, StringComparison.Ordinal);
        if (!changed)
        {
            NotifyStateChanged();
            if (_sharedContextMismatch && _currentProvider.Length > 0 && !_shared!.IsLoading && !_isShuttingDown)
                _ = RefreshSharedContextAsync();
            return;
        }
        _lastSharedWorkspace = saved.WorkspaceRoot;
        _lastSharedTrustedRoots = saved.TrustedWorkspaceRoots;
        _sharedContextMismatch = true;
        Editor.InvalidateAgentEditorContext();
        if (Editor.IsCheckingCli) Editor.CancelCliCheckCommand.Execute(null);
        NotifyStateChanged();
        if (_currentProvider.Length > 0 && !_shared!.IsLoading && !_isShuttingDown)
            _ = RefreshSharedContextAsync();
    }

    private void Control_Loaded(object sender, RoutedEventArgs e)
    {
        if (_editorEventsAttached) return;
        Editor.PropertyChanged += Editor_PropertyChanged;
        Editor.ModelCatalog.PropertyChanged += ModelCatalog_PropertyChanged;
        _editorEventsAttached = true;
        NotifyStateChanged();
    }

    private void ModelCatalog_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Editor.ModelCatalog.IsLoading) or nameof(Editor.ModelCatalog.Status))
            NotifyStateChanged();
    }

    private void Control_Unloaded(object sender, RoutedEventArgs e)
    {
        DetachEditorEvents();
    }

    private void DetachEditorEvents()
    {
        if (!_editorEventsAttached) return;
        Editor.PropertyChanged -= Editor_PropertyChanged;
        Editor.ModelCatalog.PropertyChanged -= ModelCatalog_PropertyChanged;
        _editorEventsAttached = false;
    }

    private void SetBusy(bool value)
    {
        if (_isBusy == value) return;
        _isBusy = value;
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CheckStatusLabel));
        OnPropertyChanged(nameof(CheckStatusGlyph));
        OnPropertyChanged(nameof(CheckStatusBrush));
        OnPropertyChanged(nameof(HasPendingEdits));
        OnPropertyChanged(nameof(CanChangeSelection));
        OnPropertyChanged(nameof(CanRunCliCheck));
        OnPropertyChanged(nameof(CanUseAgentProfile));
        OnPropertyChanged(nameof(IsActiveProvider));
        OnPropertyChanged(nameof(HasUnappliedChanges));
        OnPropertyChanged(nameof(ArgumentsVisibility));
        OnPropertyChanged(nameof(ShowDraftSaveError));
    }

    public bool ShowDraftSaveError => _showDraftSaveError;

    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new(propertyName));

    public static Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility StringToVisibility(string? value) => string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
}
