using ChatAgentBridge.Transports;
using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    private CancellationTokenSource? _mattermostThreadLookupCancellation;
    private string? _threadLookupServerUrl;
    private string? _threadLookupToken;
    private string? _threadLookupChannelId;
    private string? _threadLookupInput;
    private bool _threadLookupBypassProxy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMattermostThreadLookupResult))]
    [NotifyPropertyChangedFor(nameof(MattermostThreadLookupResultText))]
    [NotifyPropertyChangedFor(nameof(CanAddMattermostThreadLookupResult))]
    public partial MattermostThreadIdentity? MattermostThreadLookupResult { get; set; }
    partial void OnMattermostThreadLookupResultChanged(MattermostThreadIdentity? value) =>
        AddMattermostThreadCommand.NotifyCanExecuteChanged();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanResolveMattermostThread))]
    public partial string MattermostThreadLookupInput { get; set; } = string.Empty;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasMattermostThreadLookupStatus))]
    public partial string MattermostThreadLookupStatus { get; set; } = string.Empty;
    public bool HasMattermostThreadLookupStatus => !string.IsNullOrWhiteSpace(MattermostThreadLookupStatus);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanResolveMattermostThread))]
    [NotifyPropertyChangedFor(nameof(CanAddMattermostThreadLookupResult))]
    public partial bool IsResolvingMattermostThread { get; set; }

    public bool HasMattermostThreadLookupResult => MattermostThreadLookupResult is not null;
    public string MattermostThreadLookupResultText => MattermostThreadLookupResult is { } thread
        ? ViewModelText.Get("MattermostThreadLookupIdentity", thread.RootPostId, thread.ChannelId)
        : string.Empty;
    public bool CanResolveMattermostThread => IsSettingsLoaded && !IsLoading && !_closing &&
        !IsResolvingMattermostThread && !string.IsNullOrWhiteSpace(MattermostServerUrl) &&
        !string.IsNullOrWhiteSpace(MattermostChannelId) && !string.IsNullOrWhiteSpace(MattermostToken) &&
        !string.IsNullOrWhiteSpace(MattermostThreadLookupInput);
    public bool CanAddMattermostThreadLookupResult => HasMattermostThreadLookupResult &&
        !IsResolvingMattermostThread && !_closing && ThreadLookupInputsMatch();

    partial void OnMattermostChannelIdChanged(string value) => InvalidateMattermostThreadLookup();
    partial void OnMattermostThreadLookupInputChanged(string value) => InvalidateMattermostThreadLookup();

    [RelayCommand(CanExecute = nameof(CanResolveMattermostThread))]
    private async Task ResolveMattermostThreadAsync()
    {
        InvalidateMattermostThreadLookup();
        if (!CanResolveMattermostThread) return;

        var serverUrl = MattermostServerUrl;
        var token = MattermostToken;
        var channelId = MattermostChannelId;
        var input = MattermostThreadLookupInput;
        var cancellation = new CancellationTokenSource();
        _mattermostThreadLookupCancellation = cancellation;
        _threadLookupServerUrl = serverUrl;
        _threadLookupToken = token;
        _threadLookupChannelId = channelId;
        _threadLookupInput = input;
        _threadLookupBypassProxy = MattermostBypassProxy;
        IsResolvingMattermostThread = true;
        MattermostThreadLookupStatus = ViewModelText.Get("MattermostThreadLookupInProgress");
        OnPropertyChanged(nameof(CanAddMattermostThreadLookupResult));
        AddMattermostThreadCommand.NotifyCanExecuteChanged();
        try
        {
            using var resolver = new MattermostThreadResolver(_threadLookupBypassProxy);
            var thread = await resolver.ResolveAsync(serverUrl, token, channelId, input, cancellation.Token);
            if (cancellation.IsCancellationRequested || _closing || !ThreadLookupInputsMatch(serverUrl, token, channelId, input)) return;
            MattermostThreadLookupResult = thread;
            MattermostThreadLookupStatus = ViewModelText.Get("MattermostThreadLookupResolved");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || _closing)
        {
            // Input changes and window close invalidate an outstanding lookup.
        }
        catch (MattermostThreadLookupException ex)
        {
            if (ReferenceEquals(_mattermostThreadLookupCancellation, cancellation) && ThreadLookupInputsMatch(serverUrl, token, channelId, input))
                MattermostThreadLookupStatus = ThreadFailureText(ex.Failure);
        }
        catch (Exception) when (ReferenceEquals(_mattermostThreadLookupCancellation, cancellation) && ThreadLookupInputsMatch(serverUrl, token, channelId, input))
        {
            MattermostThreadLookupStatus = ViewModelText.Get("MattermostThreadLookupNetwork");
        }
        finally
        {
            if (ReferenceEquals(_mattermostThreadLookupCancellation, cancellation))
            {
                _mattermostThreadLookupCancellation = null;
                IsResolvingMattermostThread = false;
                if (!cancellation.IsCancellationRequested)
                    OnPropertyChanged(nameof(CanAddMattermostThreadLookupResult));
                ResolveMattermostThreadCommand.NotifyCanExecuteChanged();
                AddMattermostThreadCommand.NotifyCanExecuteChanged();
            }
            cancellation.Dispose();
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddMattermostThreadLookupResult))]
    private void AddMattermostThread()
    {
        var thread = MattermostThreadLookupResult;
        if (thread is null || !CanAddMattermostThreadLookupResult) return;

        try
        {
            var allowed = ChatAccessPolicy.NormalizeScopes(MattermostAllowedThreadIds);
            if (allowed.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(thread.RootPostId, StringComparer.Ordinal))
            {
                MattermostThreadLookupStatus = ViewModelText.Get("MattermostThreadAlreadyAllowed", thread.RootPostId);
                return;
            }

            var candidate = string.IsNullOrWhiteSpace(MattermostAllowedThreadIds)
                ? thread.RootPostId
                : MattermostAllowedThreadIds.TrimEnd() + Environment.NewLine + thread.RootPostId;
            _ = ChatAccessPolicy.NormalizeScopes(candidate);
            MattermostAllowedThreadIds = candidate;
            MattermostThreadLookupStatus = ViewModelText.Get("MattermostThreadAddedSaveNeeded", thread.RootPostId);
        }
        catch (FormatException)
        {
            MattermostThreadLookupStatus = ViewModelText.Get("MattermostThreadAllowlistInvalid");
        }
    }

    private bool ThreadLookupInputsMatch() => ThreadLookupInputsMatch(
        _threadLookupServerUrl ?? string.Empty, _threadLookupToken ?? string.Empty,
        _threadLookupChannelId ?? string.Empty, _threadLookupInput ?? string.Empty);

    private bool ThreadLookupInputsMatch(string serverUrl, string token, string channelId, string input) =>
        string.Equals(serverUrl, MattermostServerUrl, StringComparison.Ordinal) &&
        string.Equals(token, MattermostToken, StringComparison.Ordinal) &&
        _threadLookupBypassProxy == MattermostBypassProxy &&
        string.Equals(channelId, MattermostChannelId, StringComparison.Ordinal) &&
        string.Equals(input, MattermostThreadLookupInput, StringComparison.Ordinal);

    private static string ThreadFailureText(MattermostThreadLookupFailure failure) => ViewModelText.Get(failure switch
    {
        MattermostThreadLookupFailure.InvalidServerUrl => "MattermostThreadLookupInvalidUrl",
        MattermostThreadLookupFailure.InvalidLink => "MattermostThreadLookupInvalidLink",
        MattermostThreadLookupFailure.MissingCredentials => "MattermostThreadLookupMissingCredentials",
        MattermostThreadLookupFailure.InvalidToken => "MattermostThreadLookupInvalidToken",
        MattermostThreadLookupFailure.Unauthorized => "MattermostThreadLookupUnauthorized",
        MattermostThreadLookupFailure.Forbidden => "MattermostThreadLookupForbidden",
        MattermostThreadLookupFailure.NotFound => "MattermostThreadLookupNotFound",
        MattermostThreadLookupFailure.WrongChannel => "MattermostThreadLookupWrongChannel",
        MattermostThreadLookupFailure.DeletedPost => "MattermostThreadLookupDeleted",
        MattermostThreadLookupFailure.Timeout => "MattermostThreadLookupTimeout",
        MattermostThreadLookupFailure.InvalidResponse => "MattermostThreadLookupInvalidResponse",
        _ => "MattermostThreadLookupNetwork"
    });

    private void InvalidateMattermostThreadLookup()
    {
        _mattermostThreadLookupCancellation?.Cancel();
        MattermostThreadLookupResult = null;
        _threadLookupServerUrl = _threadLookupToken = _threadLookupChannelId = _threadLookupInput = null;
        MattermostThreadLookupStatus = string.Empty;
        OnPropertyChanged(nameof(CanResolveMattermostThread));
        OnPropertyChanged(nameof(CanAddMattermostThreadLookupResult));
        ResolveMattermostThreadCommand.NotifyCanExecuteChanged();
        AddMattermostThreadCommand.NotifyCanExecuteChanged();
    }

    public void CancelMattermostThreadLookup()
    {
        _mattermostThreadLookupCancellation?.Cancel();
        InvalidateMattermostThreadLookup();
    }

    public void RefreshMattermostThreadLookupCommands()
    {
        ResolveMattermostThreadCommand.NotifyCanExecuteChanged();
        AddMattermostThreadCommand.NotifyCanExecuteChanged();
    }
}
