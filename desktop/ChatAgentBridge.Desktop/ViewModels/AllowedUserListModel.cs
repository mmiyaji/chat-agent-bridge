using System.Collections.ObjectModel;
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

    internal AllowedUserRow(string provider, string userId, AllowedUserRecord? record, string? pendingName, Action<AllowedUserRow> remove)
    {
        Provider = provider;
        UserId = userId;
        _record = record;
        _pendingName = pendingName;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Provider { get; }
    public string UserId { get; }
    internal AllowedUserRecord? Record => _record;

    [ObservableProperty]
    public partial string? AvatarPath { get; set; }

    public IRelayCommand RemoveCommand { get; }

    private string? Name => string.IsNullOrWhiteSpace(_record?.DisplayName) ? _pendingName : _record.DisplayName;
    public bool HasName => !string.IsNullOrWhiteSpace(Name);
    public string DisplayName => HasName ? Name! : ViewModelText.Get("AllowedUserNameUnknown");
    public string PictureName => HasName ? Name! : UserId;
    public string IdLine => ViewModelText.Get("AllowedUserIdLine", UserId, SourceLabel);
    public string AddedLine => _record is null
        ? ViewModelText.Get(_pendingName is null ? "AllowedUserAddedUnknown" : "AllowedUserNotSaved")
        : ViewModelText.Get(_record.Method == AllowedUserDirectory.ChatRequestMethod ? "AllowedUserAddedFromChat" : "AllowedUserAddedInSettings",
            _record.AddedAt.ToLocalTime().ToString("g"),
            string.IsNullOrWhiteSpace(_record.Actor) ? ViewModelText.Get("AllowedUserActorUnknown") : _record.Actor);
    public bool HasLastRequest => _record?.LastRequestAt is not null;
    public string LastRequestLine => _record?.LastRequestAt is { } at ? ViewModelText.Get("AllowedUserLastRequest", at.ToLocalTime().ToString("g")) : "";
    public string RemoveLabel => ViewModelText.Get("AllowedUserRemove", HasName ? $"{DisplayName} ({UserId})" : UserId);

    private string SourceLabel
    {
        get
        {
            var service = Provider switch { "slack" => "Slack", "mattermost" => "Mattermost", "discord" => "Discord", _ => Provider };
            if (Provider == "mattermost" && Uri.TryCreate(_record?.Origin, UriKind.Absolute, out var server)) return $"{service} ({server.Host})";
            return service;
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
    private IReadOnlyDictionary<string, AllowedUserRecord> _records = new Dictionary<string, AllowedUserRecord>();

    internal AllowedUserListModel(string provider, Func<string> read, Action<string> write, Func<string, string> normalize)
    {
        Provider = provider;
        _read = read;
        _write = write;
        _normalize = normalize;
    }

    public string Provider { get; }
    public ObservableCollection<AllowedUserRow> Rows { get; } = [];
    public bool IsEmpty => Rows.Count == 0;
    public bool HasRows => Rows.Count > 0;

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
        var ids = SplitIds(_read());
        Rows.Clear();
        foreach (var id in ids)
        {
            _records.TryGetValue(id, out var record);
            _pendingNames.TryGetValue(id, out var pendingName);
            Rows.Add(new AllowedUserRow(Provider, id, record, pendingName, Remove));
        }
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasRows));
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
