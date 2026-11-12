using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatAgentBridge_Desktop.ViewModels;

/// <summary>One allowed user shown with the details recorded for it.</summary>
public sealed partial class AllowedUserRow : ObservableObject
{
    private readonly AllowedUserRecord? _record;

    private readonly string? _pendingName;
    private readonly string? _resolvedName;

    internal AllowedUserRow(string provider, string userId, AllowedUserRecord? record, string? pendingName, string? resolvedName, Action<AllowedUserRow> remove)
    {
        Provider = provider;
        UserId = userId;
        _record = record;
        _pendingName = pendingName;
        _resolvedName = resolvedName;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Provider { get; }
    public string UserId { get; }
    internal AllowedUserRecord? Record => _record;

    [ObservableProperty]
    public partial string? AvatarPath { get; set; }

    public IRelayCommand RemoveCommand { get; }

    private string? Name => !string.IsNullOrWhiteSpace(_resolvedName) ? _resolvedName
        : !string.IsNullOrWhiteSpace(_pendingName) ? _pendingName
        : !string.IsNullOrWhiteSpace(_record?.DisplayName) ? _record.DisplayName
        : null;
    public bool HasName => !string.IsNullOrWhiteSpace(Name);
    public string DisplayName => HasName ? Name! : ViewModelText.Get("AllowedUserNameUnknown");
    public string PictureName => HasName ? Name! : UserId;
    public string Summary => Presentation.UserIdentityText.Format(DisplayName, UserId);
    public string Details => string.Join(Environment.NewLine, ViewModelText.Get("AllowedUserIdLine", UserId, SourceLabel), AddedLine, LastRequestLine);

    private string AddedLine => _record is null
        ? ViewModelText.Get(_pendingName is null ? "AllowedUserAddedUnknown" : "AllowedUserNotSaved")
        : ViewModelText.Get(_record.Method == AllowedUserDirectory.ChatRequestMethod ? "AllowedUserAddedFromChat" : "AllowedUserAddedInSettings",
            _record.AddedAt.ToLocalTime().ToString("g"),
            string.IsNullOrWhiteSpace(_record.Actor) ? ViewModelText.Get("AllowedUserActorUnknown") : _record.Actor);
    private string LastRequestLine => _record?.LastRequestAt is { } at
        ? ViewModelText.Get("AllowedUserLastRequest", at.ToLocalTime().ToString("g"))
        : ViewModelText.Get("AllowedUserLastRequestUnknown");
    public string HistoryLabel => ViewModelText.Get("AllowedUserHistory", HasName ? $"{DisplayName} ({UserId})" : UserId);
    public string RemoveLabel => ViewModelText.Get("AllowedUserRemove", HasName ? $"{DisplayName} ({UserId})" : UserId);

    public string ServiceName => Provider switch { "slack" => "Slack", "mattermost" => "Mattermost", "discord" => "Discord", _ => Provider };

    private string SourceLabel
    {
        get
        {
            if (Provider == "mattermost" && Uri.TryCreate(_record?.Origin, UriKind.Absolute, out var server)) return $"{ServiceName} ({server.Host})";
            return ServiceName;
        }
    }
}

/// <summary>
/// The allowed users of one chat service, built from the settings draft. Adding or removing edits the
/// draft, so the change is saved, reviewed and audited with the rest of the chat settings.
/// </summary>
public sealed partial class AllowedUserListModel : ObservableObject
{
    private readonly Func<string> _read;
    private readonly Action<string> _write;
    private readonly Func<string, string> _normalize;
    private readonly Dictionary<string, string> _pendingNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _resolvedNames = new(StringComparer.Ordinal);
    private string? _resolvedNameScope;
    private IReadOnlyDictionary<string, AllowedUserRecord> _records = new Dictionary<string, AllowedUserRecord>();

    internal AllowedUserListModel(string provider, Func<string> read, Action<string> write, Func<string, string> normalize)
    {
        Provider = provider;
        _read = read;
        _write = write;
        _normalize = normalize;
    }

    public string Provider { get; }
    public IReadOnlyList<AllowedUserRow> Rows { get; private set; } = [];
    public IReadOnlyList<AllowedUserRow> PreviewRows => Rows.Take(3).ToArray();
    public bool IsEmpty => Rows.Count == 0;
    public string TotalLabel => ViewModelText.Get("AllowedUserTotal", Rows.Count);

    internal void RefreshLocalizedLabels() => OnPropertyChanged(nameof(TotalLabel));

    [ObservableProperty]
    public partial string NewUserId { get; set; } = "";

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    public bool HasStatus => !string.IsNullOrEmpty(Status);

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    /// <summary>Names learned from a username search, recorded when the settings are saved.</summary>
    internal IReadOnlyDictionary<string, string> PendingNames => _pendingNames;

    internal void RememberName(string userId, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name)) _pendingNames[userId] = name.Trim();
    }

    internal void ClearPendingNames() => _pendingNames.Clear();

    internal void SetResolvedNames(string scope, IReadOnlyDictionary<string, string> names)
    {
        if (!string.Equals(_resolvedNameScope, scope, StringComparison.Ordinal))
        {
            _resolvedNames.Clear();
            _resolvedNameScope = scope;
        }
        foreach (var (id, name) in names)
            if (!string.IsNullOrWhiteSpace(name)) _resolvedNames[id] = name.Trim();
        Refresh();
    }

    internal void SetRecords(IEnumerable<AllowedUserRecord> records)
    {
        _records = records.Where(record => record.Provider == Provider)
            .GroupBy(record => record.UserId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        Refresh();
    }

    /// <summary>Rebuilds the rows from the draft. Invalid text is still listed by its parts so it can be removed.</summary>
    internal void Refresh()
    {
        Rows = SplitIds(_read()).Select(id =>
        {
            _records.TryGetValue(id, out var record);
            _pendingNames.TryGetValue(id, out var pendingName);
            _resolvedNames.TryGetValue(id, out var resolvedName);
            return new AllowedUserRow(Provider, id, record, pendingName, resolvedName, Remove);
        }).ToArray();
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(PreviewRows));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(TotalLabel));
    }

    [RelayCommand]
    private void Add()
    {
        var id = NewUserId.Trim();
        if (id.Length == 0) return;
        var ids = SplitIds(_read());
        if (ids.Contains(id, StringComparer.Ordinal))
        {
            Status = ViewModelText.Get("AllowedUserAlreadyListed", id);
            return;
        }
        var candidate = string.Join(Environment.NewLine, ids.Append(id));
        try { _ = _normalize(candidate); }
        catch (FormatException)
        {
            Status = ViewModelText.Get(Provider == "discord" ? "AllowedUserInvalidDiscordId" : "AllowedUserInvalidId");
            return;
        }
        _write(candidate);
        NewUserId = "";
        Status = ViewModelText.Get("AllowedUserAddedSaveNeeded", id);
    }

    private void Remove(AllowedUserRow row)
    {
        var remaining = SplitIds(_read()).Where(id => !string.Equals(id, row.UserId, StringComparison.Ordinal));
        _write(string.Join(Environment.NewLine, remaining));
        Status = ViewModelText.Get("AllowedUserRemovedSaveNeeded", row.HasName ? row.DisplayName : row.UserId);
    }

    internal static string[] SplitIds(string value) => value.Split([',', ';', ' ', '\t', '\r', '\n'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
}
