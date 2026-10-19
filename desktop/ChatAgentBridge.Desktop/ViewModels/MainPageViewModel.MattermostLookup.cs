using ChatAgentBridge.Core;
using ChatAgentBridge.Transports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    private CancellationTokenSource? _mattermostLookupCancellation;
    private string? _lookupServerUrl;
    private string? _lookupToken;
    private string? _lookupUsername;
    private bool _lookupBypassProxy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMattermostUsernameLookupResult))]
    [NotifyPropertyChangedFor(nameof(MattermostUsernameLookupResultText))]
    [NotifyPropertyChangedFor(nameof(CanAddMattermostUsernameLookupResult))]
    public partial MattermostUserIdentity? MattermostUsernameLookupResult { get; set; }
    partial void OnMattermostUsernameLookupResultChanged(MattermostUserIdentity? value) =>
        AddMattermostUsernameLookupResultCommand.NotifyCanExecuteChanged();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLookupMattermostUsername))]
    public partial string MattermostUsernameLookupInput { get; set; } = string.Empty;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasMattermostUsernameLookupStatus))]
    public partial string MattermostUsernameLookupStatus { get; set; } = string.Empty;
    public bool HasMattermostUsernameLookupStatus => !string.IsNullOrWhiteSpace(MattermostUsernameLookupStatus);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLookupMattermostUsername))]
    [NotifyPropertyChangedFor(nameof(CanAddMattermostUsernameLookupResult))]
    public partial bool IsLookingUpMattermostUsername { get; set; }

    public bool HasMattermostUsernameLookupResult => MattermostUsernameLookupResult is not null;
    public string MattermostUsernameLookupResultText => MattermostUsernameLookupResult is { } user
        ? ViewModelText.Get("MattermostUsernameLookupIdentity", user.Username, user.DisplayName, user.Id)
        : string.Empty;
    public bool CanLookupMattermostUsername => IsSettingsLoaded && !IsLoading && !_closing &&
        !IsLookingUpMattermostUsername && !string.IsNullOrWhiteSpace(MattermostServerUrl) &&
        !string.IsNullOrWhiteSpace(MattermostToken) && !string.IsNullOrWhiteSpace(MattermostUsernameLookupInput);
    public bool CanAddMattermostUsernameLookupResult => HasMattermostUsernameLookupResult &&
        !IsLookingUpMattermostUsername && !_closing &&
        string.Equals(_lookupServerUrl, MattermostServerUrl, StringComparison.Ordinal) &&
        string.Equals(_lookupToken, MattermostToken, StringComparison.Ordinal) &&
        _lookupBypassProxy == MattermostBypassProxy &&
        string.Equals(_lookupUsername, MattermostUsernameLookupInput, StringComparison.Ordinal);

    partial void OnMattermostServerUrlChanged(string value)
    {
        InvalidateMattermostUsernameLookup();
        InvalidateMattermostThreadLookup();
    }
    partial void OnMattermostUsernameLookupInputChanged(string value) => InvalidateMattermostUsernameLookup();

    partial void OnMattermostBypassProxyChanged(bool value)
    {
        InvalidateMattermostUsernameLookup();
        InvalidateMattermostThreadLookup();
    }

    [RelayCommand(CanExecute = nameof(CanLookupMattermostUsername))]
    private async Task LookupMattermostUsernameAsync()
    {
        InvalidateMattermostUsernameLookup();
        if (!CanLookupMattermostUsername) return;

        var serverUrl = MattermostServerUrl;
        var token = MattermostToken;
        var username = MattermostUsernameLookupInput;
        var cancellation = new CancellationTokenSource();
        _mattermostLookupCancellation = cancellation;
        _lookupServerUrl = serverUrl;
        _lookupToken = token;
        _lookupUsername = username;
        _lookupBypassProxy = MattermostBypassProxy;
        IsLookingUpMattermostUsername = true;
        MattermostUsernameLookupStatus = ViewModelText.Get("MattermostUsernameLookupInProgress");
        OnPropertyChanged(nameof(CanAddMattermostUsernameLookupResult));
        AddMattermostUsernameLookupResultCommand.NotifyCanExecuteChanged();
        try
        {
            using var resolver = new MattermostUsernameResolver(_lookupBypassProxy);
            var user = await resolver.ResolveAsync(serverUrl, token, username, cancellation.Token);
            if (cancellation.IsCancellationRequested || _closing || !LookupInputsMatch(serverUrl, token, username)) return;
            MattermostUsernameLookupResult = user;
            MattermostUsernameLookupStatus = ViewModelText.Get("MattermostUsernameLookupFound");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || _closing)
        {
            // Input changes and window close invalidate an outstanding lookup.
        }
        catch (MattermostUsernameLookupException ex)
        {
            if (ReferenceEquals(_mattermostLookupCancellation, cancellation) && LookupInputsMatch(serverUrl, token, username))
                MattermostUsernameLookupStatus = LookupFailureText(ex.Failure);
        }
        catch (Exception) when (ReferenceEquals(_mattermostLookupCancellation, cancellation) && LookupInputsMatch(serverUrl, token, username))
        {
            MattermostUsernameLookupStatus = ViewModelText.Get("MattermostUsernameLookupNetwork");
        }
        finally
        {
            if (ReferenceEquals(_mattermostLookupCancellation, cancellation))
            {
                _mattermostLookupCancellation = null;
                IsLookingUpMattermostUsername = false;
                if (!cancellation.IsCancellationRequested)
                    OnPropertyChanged(nameof(CanAddMattermostUsernameLookupResult));
                LookupMattermostUsernameCommand.NotifyCanExecuteChanged();
                AddMattermostUsernameLookupResultCommand.NotifyCanExecuteChanged();
            }
            cancellation.Dispose();
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddMattermostUsernameLookupResult))]
    private void AddMattermostUsernameLookupResult()
    {
        var user = MattermostUsernameLookupResult;
        if (user is null || !CanAddMattermostUsernameLookupResult) return;
        try
        {
            var allowed = ChatAccessPolicy.NormalizeAllowedUsers(MattermostAllowedUserIds);
            if (allowed.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(user.Id, StringComparer.Ordinal))
            {
                MattermostUsernameLookupStatus = ViewModelText.Get("MattermostUsernameAlreadyAllowed", user.Username);
                return;
            }

            var candidate = string.IsNullOrWhiteSpace(MattermostAllowedUserIds)
                ? user.Id
                : MattermostAllowedUserIds.TrimEnd() + Environment.NewLine + user.Id;
            _ = ChatAccessPolicy.NormalizeAllowedUsers(candidate);
            MattermostAllowedUserIds = candidate;
            MattermostUsernameLookupStatus = ViewModelText.Get("MattermostUsernameAddedSaveNeeded", user.Username);
        }
        catch (FormatException)
        {
            MattermostUsernameLookupStatus = ViewModelText.Get("MattermostUsernameAllowlistInvalid");
        }
    }

    private void InvalidateMattermostUsernameLookup()
    {
        _mattermostLookupCancellation?.Cancel();
        MattermostUsernameLookupResult = null;
        _lookupServerUrl = _lookupToken = _lookupUsername = null;
        MattermostUsernameLookupStatus = string.Empty;
        OnPropertyChanged(nameof(CanLookupMattermostUsername));
        OnPropertyChanged(nameof(CanAddMattermostUsernameLookupResult));
        LookupMattermostUsernameCommand.NotifyCanExecuteChanged();
        AddMattermostUsernameLookupResultCommand.NotifyCanExecuteChanged();
    }

    private bool LookupInputsMatch(string serverUrl, string token, string username) =>
        string.Equals(serverUrl, MattermostServerUrl, StringComparison.Ordinal) &&
        string.Equals(token, MattermostToken, StringComparison.Ordinal) &&
        _lookupBypassProxy == MattermostBypassProxy &&
        string.Equals(username, MattermostUsernameLookupInput, StringComparison.Ordinal);

    private static string LookupFailureText(MattermostUsernameLookupFailure failure) => ViewModelText.Get(failure switch
    {
        MattermostUsernameLookupFailure.InvalidServerUrl => "MattermostUsernameLookupInvalidUrl",
        MattermostUsernameLookupFailure.InvalidUsername => "MattermostUsernameLookupInvalidUsername",
        MattermostUsernameLookupFailure.MissingCredentials => "MattermostUsernameLookupMissingCredentials",
        MattermostUsernameLookupFailure.InvalidToken => "MattermostUsernameLookupInvalidToken",
        MattermostUsernameLookupFailure.Unauthorized => "MattermostUsernameLookupUnauthorized",
        MattermostUsernameLookupFailure.Forbidden => "MattermostUsernameLookupForbidden",
        MattermostUsernameLookupFailure.NotFound => "MattermostUsernameLookupNotFound",
        MattermostUsernameLookupFailure.Timeout => "MattermostUsernameLookupTimeout",
        MattermostUsernameLookupFailure.NotAnActivePerson => "MattermostUsernameLookupNotActive",
        MattermostUsernameLookupFailure.InvalidResponse => "MattermostUsernameLookupInvalidResponse",
        _ => "MattermostUsernameLookupNetwork"
    });

    public void CancelMattermostUsernameLookup()
    {
        _mattermostLookupCancellation?.Cancel();
        InvalidateMattermostUsernameLookup();
    }

    public void RefreshMattermostUsernameLookupCommands()
    {
        LookupMattermostUsernameCommand.NotifyCanExecuteChanged();
        AddMattermostUsernameLookupResultCommand.NotifyCanExecuteChanged();
    }
}
