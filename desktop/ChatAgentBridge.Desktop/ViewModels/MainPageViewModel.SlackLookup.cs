using ChatAgentBridge.Core;
using ChatAgentBridge.Transports;
using ChatAgentBridge_Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    private CancellationTokenSource? _slackUsernameLookupCancellation;
    private CancellationTokenSource? _slackThreadLookupCancellation;
    private string? _slackUsernameLookupToken;
    private string? _slackUsernameLookupInput;
    private string? _slackThreadLookupToken;
    private string? _slackThreadLookupInput;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSlackUsernameLookupResult))]
    [NotifyPropertyChangedFor(nameof(SlackUsernameLookupResultText))]
    [NotifyPropertyChangedFor(nameof(CanAddSlackUsernameLookupResult))]
    public partial SlackUserIdentity? SlackUsernameLookupResult { get; set; }
    partial void OnSlackUsernameLookupResultChanged(SlackUserIdentity? value) =>
        AddSlackUsernameLookupResultCommand.NotifyCanExecuteChanged();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLookupSlackUsername))]
    public partial string SlackUsernameLookupInput { get; set; } = string.Empty;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSlackUsernameLookupStatus))]
    public partial string SlackUsernameLookupStatus { get; set; } = string.Empty;
    public bool HasSlackUsernameLookupStatus => !string.IsNullOrWhiteSpace(SlackUsernameLookupStatus);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLookupSlackUsername))]
    [NotifyPropertyChangedFor(nameof(CanAddSlackUsernameLookupResult))]
    public partial bool IsLookingUpSlackUsername { get; set; }

    public bool HasSlackUsernameLookupResult => SlackUsernameLookupResult is not null;
    public string SlackUsernameLookupResultText => SlackUsernameLookupResult is { } user
        ? ViewModelText.Get("SlackUsernameLookupIdentity", user.Name, user.RealName, user.Id, user.TeamName, user.TeamId)
        : string.Empty;
    public bool CanLookupSlackUsername => IsSettingsLoaded && !IsLoading && !_closing &&
        !IsLookingUpSlackUsername && !string.IsNullOrWhiteSpace(SlackBotToken) &&
        !string.IsNullOrWhiteSpace(SlackUsernameLookupInput);
    public bool CanAddSlackUsernameLookupResult => HasSlackUsernameLookupResult && IsSettingsLoaded && !IsLoading && !_closing &&
        !IsLookingUpSlackUsername && string.Equals(_slackUsernameLookupToken, SlackBotToken, StringComparison.Ordinal) &&
        string.Equals(_slackUsernameLookupInput, SlackUsernameLookupInput, StringComparison.Ordinal);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSlackThreadLookupResult))]
    [NotifyPropertyChangedFor(nameof(SlackThreadLookupResultText))]
    [NotifyPropertyChangedFor(nameof(CanAddSlackThreadLookupResult))]
    public partial SlackThreadIdentity? SlackThreadLookupResult { get; set; }
    partial void OnSlackThreadLookupResultChanged(SlackThreadIdentity? value) =>
        AddSlackThreadLookupResultCommand.NotifyCanExecuteChanged();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanResolveSlackThread))]
    public partial string SlackThreadLookupInput { get; set; } = string.Empty;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSlackThreadLookupStatus))]
    public partial string SlackThreadLookupStatus { get; set; } = string.Empty;
    public bool HasSlackThreadLookupStatus => !string.IsNullOrWhiteSpace(SlackThreadLookupStatus);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanResolveSlackThread))]
    [NotifyPropertyChangedFor(nameof(CanAddSlackThreadLookupResult))]
    public partial bool IsResolvingSlackThread { get; set; }

    public bool HasSlackThreadLookupResult => SlackThreadLookupResult is not null;
    public string SlackThreadLookupResultText => SlackThreadLookupResult is { } thread
        ? ViewModelText.Get("SlackThreadLookupIdentity", thread.TeamName, SlackChannelLabel(thread), thread.RootTimestamp)
        : string.Empty;
    public bool CanResolveSlackThread => IsSettingsLoaded && !IsLoading && !_closing && !IsResolvingSlackThread &&
        !string.IsNullOrWhiteSpace(SlackBotToken) && !string.IsNullOrWhiteSpace(SlackThreadLookupInput);
    public bool CanAddSlackThreadLookupResult => HasSlackThreadLookupResult && IsSettingsLoaded && !IsLoading && !_closing &&
        !IsResolvingSlackThread && string.Equals(_slackThreadLookupToken, SlackBotToken, StringComparison.Ordinal) &&
        string.Equals(_slackThreadLookupInput, SlackThreadLookupInput, StringComparison.Ordinal);

    partial void OnSlackBotTokenChanged(string value)
    {
        InvalidateSlackUsernameLookup();
        InvalidateSlackThreadLookup();
    }
    partial void OnSlackAppTokenChanged(string value)
    {
        InvalidateSlackUsernameLookup();
        InvalidateSlackThreadLookup();
    }
    partial void OnSlackUsernameLookupInputChanged(string value) => InvalidateSlackUsernameLookup();
    partial void OnSlackThreadLookupInputChanged(string value) => InvalidateSlackThreadLookup();

    [RelayCommand(CanExecute = nameof(CanLookupSlackUsername))]
    private async Task LookupSlackUsernameAsync()
    {
        InvalidateSlackUsernameLookup();
        if (!CanLookupSlackUsername) return;

        var token = SlackBotToken;
        var input = SlackUsernameLookupInput;
        var cancellation = new CancellationTokenSource();
        _slackUsernameLookupCancellation = cancellation;
        _slackUsernameLookupToken = token;
        _slackUsernameLookupInput = input;
        IsLookingUpSlackUsername = true;
        SlackUsernameLookupStatus = ViewModelText.Get("SlackUsernameLookupInProgress");
        OnPropertyChanged(nameof(CanAddSlackUsernameLookupResult));
        AddSlackUsernameLookupResultCommand.NotifyCanExecuteChanged();
        try
        {
            using var resolver = new SlackUsernameResolver();
            var user = await resolver.ResolveAsync(token, input, cancellation.Token);
            if (cancellation.IsCancellationRequested || _closing || !SlackUsernameInputsMatch(token, input)) return;
            SlackUsernameLookupResult = user;
            SlackUsernameLookupStatus = ViewModelText.Get("SlackUsernameLookupFound");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || _closing)
        {
            // Input or credentials changed while the lookup was in flight.
        }
        catch (SlackUsernameLookupException ex)
        {
            if (ReferenceEquals(_slackUsernameLookupCancellation, cancellation) && SlackUsernameInputsMatch(token, input))
                SlackUsernameLookupStatus = SlackUsernameFailureText(ex);
        }
        catch (Exception) when (ReferenceEquals(_slackUsernameLookupCancellation, cancellation) && SlackUsernameInputsMatch(token, input))
        {
            SlackUsernameLookupStatus = ViewModelText.Get("SlackUsernameLookupNetwork");
        }
        finally
        {
            if (ReferenceEquals(_slackUsernameLookupCancellation, cancellation))
            {
                _slackUsernameLookupCancellation = null;
                IsLookingUpSlackUsername = false;
                if (!cancellation.IsCancellationRequested) OnPropertyChanged(nameof(CanAddSlackUsernameLookupResult));
                LookupSlackUsernameCommand.NotifyCanExecuteChanged();
                AddSlackUsernameLookupResultCommand.NotifyCanExecuteChanged();
            }
            cancellation.Dispose();
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddSlackUsernameLookupResult))]
    private void AddSlackUsernameLookupResult()
    {
        var user = SlackUsernameLookupResult;
        if (user is null || !CanAddSlackUsernameLookupResult) return;
        try
        {
            var allowed = ChatAccessPolicy.NormalizeAllowedUsers(SlackAllowedUserIds);
            if (allowed.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(user.Id, StringComparer.Ordinal))
            {
                SlackUsernameLookupStatus = ViewModelText.Get("SlackUsernameAlreadyAllowed", user.Name);
                return;
            }

            var candidate = string.IsNullOrWhiteSpace(SlackAllowedUserIds)
                ? user.Id
                : SlackAllowedUserIds.TrimEnd() + Environment.NewLine + user.Id;
            _ = ChatAccessPolicy.NormalizeAllowedUsers(candidate);
            SlackAllowedUserIds = candidate;
            SlackUsernameLookupStatus = ViewModelText.Get("SlackUsernameAddedSaveNeeded", user.Name);
        }
        catch (FormatException)
        {
            SlackUsernameLookupStatus = ViewModelText.Get("SlackUsernameAllowlistInvalid");
        }
    }

    [RelayCommand(CanExecute = nameof(CanResolveSlackThread))]
    private async Task ResolveSlackThreadAsync()
    {
        InvalidateSlackThreadLookup();
        if (!CanResolveSlackThread) return;

        var token = SlackBotToken;
        var input = SlackThreadLookupInput;
        var cancellation = new CancellationTokenSource();
        _slackThreadLookupCancellation = cancellation;
        _slackThreadLookupToken = token;
        _slackThreadLookupInput = input;
        IsResolvingSlackThread = true;
        SlackThreadLookupStatus = ViewModelText.Get("SlackThreadLookupInProgress");
        OnPropertyChanged(nameof(CanAddSlackThreadLookupResult));
        AddSlackThreadLookupResultCommand.NotifyCanExecuteChanged();
        try
        {
            using var resolver = new SlackThreadResolver();
            var thread = await resolver.ResolveAsync(token, input, cancellation.Token);
            if (cancellation.IsCancellationRequested || _closing || !SlackThreadInputsMatch(token, input)) return;
            SlackThreadLookupResult = thread;
            SlackThreadLookupStatus = ViewModelText.Get("SlackThreadLookupResolved");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || _closing)
        {
            // Input or credentials changed while the lookup was in flight.
        }
        catch (SlackThreadLookupException ex)
        {
            if (ReferenceEquals(_slackThreadLookupCancellation, cancellation) && SlackThreadInputsMatch(token, input))
                SlackThreadLookupStatus = SlackThreadFailureText(ex);
        }
        catch (Exception) when (ReferenceEquals(_slackThreadLookupCancellation, cancellation) && SlackThreadInputsMatch(token, input))
        {
            SlackThreadLookupStatus = ViewModelText.Get("SlackThreadLookupNetwork");
        }
        finally
        {
            if (ReferenceEquals(_slackThreadLookupCancellation, cancellation))
            {
                _slackThreadLookupCancellation = null;
                IsResolvingSlackThread = false;
                if (!cancellation.IsCancellationRequested) OnPropertyChanged(nameof(CanAddSlackThreadLookupResult));
                ResolveSlackThreadCommand.NotifyCanExecuteChanged();
                AddSlackThreadLookupResultCommand.NotifyCanExecuteChanged();
            }
            cancellation.Dispose();
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddSlackThreadLookupResult))]
    private void AddSlackThreadLookupResult()
    {
        var thread = SlackThreadLookupResult;
        if (thread is null || !CanAddSlackThreadLookupResult) return;
        try
        {
            var channels = ChatAccessPolicy.NormalizeScopes(SlackAllowedChannelIds);
            var threads = ChatAccessPolicy.NormalizeScopes(SlackAllowedThreadIds);
            var hasChannel = channels.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(thread.ChannelId, StringComparer.Ordinal);
            var hasThread = threads.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(thread.RootTimestamp, StringComparer.Ordinal);
            if (hasChannel && hasThread)
            {
                SlackThreadLookupStatus = ViewModelText.Get("SlackThreadAlreadyAllowed", thread.RootTimestamp);
                return;
            }

            var nextChannels = hasChannel ? SlackAllowedChannelIds : AppendScope(SlackAllowedChannelIds, thread.ChannelId);
            var nextThreads = hasThread ? SlackAllowedThreadIds : AppendScope(SlackAllowedThreadIds, thread.RootTimestamp);
            _ = ChatAccessPolicy.NormalizeScopes(nextChannels);
            _ = ChatAccessPolicy.NormalizeScopes(nextThreads);
            SlackAllowedChannelIds = nextChannels;
            SlackAllowedThreadIds = nextThreads;
            SlackThreadLookupStatus = ViewModelText.Get("SlackThreadAddedSaveNeeded", SlackChannelLabel(thread), thread.RootTimestamp);
        }
        catch (FormatException)
        {
            SlackThreadLookupStatus = ViewModelText.Get("SlackThreadAllowlistInvalid");
        }
    }

    private static string AppendScope(string existing, string id) =>
        string.IsNullOrWhiteSpace(existing) ? id : existing.TrimEnd() + Environment.NewLine + id;

    private static string SlackChannelLabel(SlackThreadIdentity thread) =>
        string.IsNullOrWhiteSpace(thread.ChannelName) ? thread.ChannelId : $"#{thread.ChannelName} ({thread.ChannelId})";

    private bool SlackUsernameInputsMatch(string token, string input) =>
        string.Equals(token, SlackBotToken, StringComparison.Ordinal) &&
        string.Equals(input, SlackUsernameLookupInput, StringComparison.Ordinal);
    private bool SlackThreadInputsMatch(string token, string input) =>
        string.Equals(token, SlackBotToken, StringComparison.Ordinal) &&
        string.Equals(input, SlackThreadLookupInput, StringComparison.Ordinal);

    private static string SlackUsernameFailureText(SlackUsernameLookupException ex) => ViewModelText.Get(ex.Failure switch
    {
        SlackUsernameLookupFailure.InvalidUsername => "SlackUsernameLookupInvalidUsername",
        SlackUsernameLookupFailure.MissingCredentials => "SlackUsernameLookupMissingCredentials",
        SlackUsernameLookupFailure.InvalidToken => "SlackUsernameLookupInvalidToken",
        SlackUsernameLookupFailure.WrongWorkspace => "SlackUsernameLookupWrongWorkspace",
        SlackUsernameLookupFailure.MissingScope => "SlackUsernameLookupMissingScope",
        SlackUsernameLookupFailure.RateLimited => "SlackUsernameLookupRateLimited",
        SlackUsernameLookupFailure.NotFound => "SlackUsernameLookupNotFound",
        SlackUsernameLookupFailure.Ambiguous => "SlackUsernameLookupAmbiguous",
        SlackUsernameLookupFailure.Timeout => "SlackUsernameLookupTimeout",
        SlackUsernameLookupFailure.InvalidResponse => "SlackUsernameLookupInvalidResponse",
        _ => "SlackUsernameLookupNetwork"
    }, ex.RequiredScope ?? string.Empty);

    private static string SlackThreadFailureText(SlackThreadLookupException ex) => ViewModelText.Get(ex.Failure switch
    {
        SlackThreadLookupFailure.InvalidLink => "SlackThreadLookupInvalidLink",
        SlackThreadLookupFailure.MissingCredentials => "SlackThreadLookupMissingCredentials",
        SlackThreadLookupFailure.InvalidToken => "SlackThreadLookupInvalidToken",
        SlackThreadLookupFailure.WrongWorkspace => "SlackThreadLookupWrongWorkspace",
        SlackThreadLookupFailure.MissingScope => "SlackThreadLookupMissingScope",
        SlackThreadLookupFailure.RateLimited => "SlackThreadLookupRateLimited",
        SlackThreadLookupFailure.NotFound => "SlackThreadLookupNotFound",
        SlackThreadLookupFailure.WrongChannel => "SlackThreadLookupWrongChannel",
        SlackThreadLookupFailure.DeletedMessage => "SlackThreadLookupDeleted",
        SlackThreadLookupFailure.Timeout => "SlackThreadLookupTimeout",
        SlackThreadLookupFailure.InvalidResponse => "SlackThreadLookupInvalidResponse",
        _ => "SlackThreadLookupNetwork"
    }, ex.RequiredScope ?? string.Empty);

    private void InvalidateSlackUsernameLookup()
    {
        _slackUsernameLookupCancellation?.Cancel();
        SlackUsernameLookupResult = null;
        _slackUsernameLookupToken = _slackUsernameLookupInput = null;
        SlackUsernameLookupStatus = string.Empty;
        OnPropertyChanged(nameof(CanLookupSlackUsername));
        OnPropertyChanged(nameof(CanAddSlackUsernameLookupResult));
        LookupSlackUsernameCommand.NotifyCanExecuteChanged();
        AddSlackUsernameLookupResultCommand.NotifyCanExecuteChanged();
    }

    private void InvalidateSlackThreadLookup()
    {
        _slackThreadLookupCancellation?.Cancel();
        SlackThreadLookupResult = null;
        _slackThreadLookupToken = _slackThreadLookupInput = null;
        SlackThreadLookupStatus = string.Empty;
        OnPropertyChanged(nameof(CanResolveSlackThread));
        OnPropertyChanged(nameof(CanAddSlackThreadLookupResult));
        ResolveSlackThreadCommand.NotifyCanExecuteChanged();
        AddSlackThreadLookupResultCommand.NotifyCanExecuteChanged();
    }

    public void CancelSlackLookups()
    {
        _slackUsernameLookupCancellation?.Cancel();
        _slackThreadLookupCancellation?.Cancel();
        InvalidateSlackUsernameLookup();
        InvalidateSlackThreadLookup();
    }

    public void RefreshSlackLookupCommands()
    {
        OnPropertyChanged(nameof(CanLookupSlackUsername));
        OnPropertyChanged(nameof(CanAddSlackUsernameLookupResult));
        OnPropertyChanged(nameof(CanResolveSlackThread));
        OnPropertyChanged(nameof(CanAddSlackThreadLookupResult));
        LookupSlackUsernameCommand.NotifyCanExecuteChanged();
        AddSlackUsernameLookupResultCommand.NotifyCanExecuteChanged();
        ResolveSlackThreadCommand.NotifyCanExecuteChanged();
        AddSlackThreadLookupResultCommand.NotifyCanExecuteChanged();
    }
}
