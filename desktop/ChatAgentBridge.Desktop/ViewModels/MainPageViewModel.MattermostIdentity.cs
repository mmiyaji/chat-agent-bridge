using ChatAgentBridge.Transports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    private CancellationTokenSource? _mattermostIdentityCancellation;
    private string? _identityServerUrl;
    private string? _identityToken;
    private bool _identityBypassProxy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMattermostConnectionIdentity))]
    [NotifyPropertyChangedFor(nameof(HasMattermostConnectionIdentityFeedback))]
    [NotifyPropertyChangedFor(nameof(MattermostConnectionIdentityText))]
    public partial MattermostConnectionIdentity? MattermostConnectionIdentity { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanCheckMattermostConnectionIdentity))]
    public partial bool IsCheckingMattermostConnectionIdentity { get; set; }

    [ObservableProperty]
    public partial string MattermostConnectionIdentityStatus { get; set; } = string.Empty;

    public bool HasMattermostConnectionIdentity => MattermostConnectionIdentity is not null;
    public bool HasMattermostConnectionIdentityFeedback => !HasMattermostConnectionIdentity && HasMattermostConnectionIdentityStatus;
    public bool CanCheckMattermostConnectionIdentity => IsSettingsLoaded && !IsLoading && !_closing &&
        !IsCheckingMattermostConnectionIdentity && !string.IsNullOrWhiteSpace(MattermostServerUrl) &&
        !string.IsNullOrWhiteSpace(MattermostToken);

    public string MattermostConnectionIdentityText
    {
        get
        {
            if (MattermostConnectionIdentity is not { } identity) return string.Empty;
            var type = ViewModelText.Get(identity.IsBot ? "MattermostAccountIdentityBot" : "MattermostAccountIdentityUser");
            return ViewModelText.Get("MattermostAccountIdentity", identity.DisplayName, identity.Username, type, identity.Id);
        }
    }

    public bool HasMattermostConnectionIdentityStatus => !string.IsNullOrWhiteSpace(MattermostConnectionIdentityStatus);
    partial void OnMattermostConnectionIdentityStatusChanged(string value)
    {
        OnPropertyChanged(nameof(HasMattermostConnectionIdentityStatus));
        OnPropertyChanged(nameof(HasMattermostConnectionIdentityFeedback));
    }

    partial void OnIsSettingsLoadedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCheckMattermostConnectionIdentity));
        CheckMattermostConnectionIdentityCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCheckMattermostConnectionIdentity))]
    private async Task CheckMattermostConnectionIdentityAsync()
    {
        InvalidateMattermostConnectionIdentity();
        if (!CanCheckMattermostConnectionIdentity) return;

        var serverUrl = MattermostServerUrl;
        var token = MattermostToken;
        var bypassProxy = MattermostBypassProxy;
        var cancellation = new CancellationTokenSource();
        _mattermostIdentityCancellation = cancellation;
        _identityServerUrl = serverUrl;
        _identityToken = token;
        _identityBypassProxy = bypassProxy;
        IsCheckingMattermostConnectionIdentity = true;
        MattermostConnectionIdentityStatus = ViewModelText.Get("MattermostAccountIdentityChecking");
        try
        {
            using var resolver = new MattermostConnectionIdentityResolver(bypassProxy);
            var identity = await resolver.ResolveAsync(serverUrl, token, cancellation.Token);
            if (cancellation.IsCancellationRequested || _closing || !IdentityInputsMatch(serverUrl, token, bypassProxy)) return;
            MattermostConnectionIdentity = identity;
            MattermostConnectionIdentityStatus = ViewModelText.Get("MattermostAccountIdentityVerified");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || _closing)
        {
            // Input changed or the settings window is closing.
        }
        catch (InvalidOperationException ex)
        {
            if (ReferenceEquals(_mattermostIdentityCancellation, cancellation) && IdentityInputsMatch(serverUrl, token, bypassProxy))
                MattermostConnectionIdentityStatus = IdentityFailureText(ex.Message);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && !_closing)
        {
            if (ReferenceEquals(_mattermostIdentityCancellation, cancellation) && IdentityInputsMatch(serverUrl, token, bypassProxy))
                MattermostConnectionIdentityStatus = ViewModelText.Get("MattermostAccountIdentityTimeout");
        }
        catch (Exception)
        {
            if (ReferenceEquals(_mattermostIdentityCancellation, cancellation) && IdentityInputsMatch(serverUrl, token, bypassProxy))
                MattermostConnectionIdentityStatus = ViewModelText.Get("MattermostAccountIdentityNetwork");
        }
        finally
        {
            if (ReferenceEquals(_mattermostIdentityCancellation, cancellation))
            {
                _mattermostIdentityCancellation = null;
                IsCheckingMattermostConnectionIdentity = false;
                CheckMattermostConnectionIdentityCommand.NotifyCanExecuteChanged();
            }
            cancellation.Dispose();
        }
    }

    public void CancelMattermostConnectionIdentityCheck()
    {
        _mattermostIdentityCancellation?.Cancel();
        InvalidateMattermostConnectionIdentity();
    }

    private void InvalidateMattermostConnectionIdentity()
    {
        _mattermostIdentityCancellation?.Cancel();
        MattermostConnectionIdentity = null;
        MattermostConnectionIdentityStatus = string.Empty;
        _identityServerUrl = _identityToken = null;
        OnPropertyChanged(nameof(CanCheckMattermostConnectionIdentity));
        CheckMattermostConnectionIdentityCommand.NotifyCanExecuteChanged();
    }

    private bool IdentityInputsMatch(string serverUrl, string token, bool bypassProxy) =>
        string.Equals(serverUrl, MattermostServerUrl, StringComparison.Ordinal) &&
        string.Equals(token, MattermostToken, StringComparison.Ordinal) && bypassProxy == MattermostBypassProxy &&
        string.Equals(_identityServerUrl, serverUrl, StringComparison.Ordinal) &&
        string.Equals(_identityToken, token, StringComparison.Ordinal) && _identityBypassProxy == bypassProxy;

    private static string IdentityFailureText(string failure) => ViewModelText.Get(failure switch
    {
        "MattermostServerUrlInvalid" => "MattermostAccountIdentityInvalidUrl",
        "MattermostTokenRequired" => "MattermostAccountIdentityMissingToken",
        "MattermostIdentityInvalidResponse" => "MattermostAccountIdentityInvalidResponse",
        _ when failure.Contains("HTTP 401", StringComparison.Ordinal) => "MattermostAccountIdentityUnauthorized",
        _ when failure.Contains("HTTP 403", StringComparison.Ordinal) => "MattermostAccountIdentityForbidden",
        _ => "MattermostAccountIdentityNetwork"
    });
}
