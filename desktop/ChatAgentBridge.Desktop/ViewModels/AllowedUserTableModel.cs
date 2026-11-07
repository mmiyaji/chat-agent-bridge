using ChatAgentBridge_Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatAgentBridge_Desktop.ViewModels;

public sealed partial class AllowedUserTableModel : ObservableObject
{
    private const int PreviewCount = 6;
    private readonly AllowedUserListModel[] _services;
    private IReadOnlyList<AllowedUserRow> _rows = [];

    internal AllowedUserTableModel(params AllowedUserListModel[] services)
    {
        _services = services;
        foreach (var service in services)
        {
            service.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(AllowedUserListModel.Rows)) Refresh();
                if (args.PropertyName == nameof(AllowedUserListModel.Status)) Status = service.Status;
            };
        }
        Refresh();
    }

    public bool IsEmpty => _rows.Count == 0;
    public bool HasRows => !IsEmpty;
    public bool HasMoreRows => _rows.Count > PreviewCount;
    public IEnumerable<AllowedUserRow> VisibleRows => IsExpanded ? _rows : _rows.Take(PreviewCount);
    public string TotalLabel => ViewModelText.Get("AllowedUserTotal", _rows.Count);
    public string ExpansionLabel => IsExpanded
        ? ViewModelText.Get("AllowedUserShowLess")
        : ViewModelText.Get("AllowedUserShowRemaining", Math.Max(0, _rows.Count - PreviewCount));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleRows))]
    [NotifyPropertyChangedFor(nameof(ExpansionLabel))]
    public partial bool IsExpanded { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string Status { get; private set; } = "";

    public bool HasStatus => !string.IsNullOrEmpty(Status);

    [RelayCommand]
    private void ToggleExpansion() => IsExpanded = !IsExpanded;

    private void Refresh()
    {
        var rows = new List<AllowedUserRow>();
        var longestList = _services.Max(service => service.Rows.Count);
        for (var index = 0; index < longestList; index++)
        {
            foreach (var service in _services)
            {
                if (index < service.Rows.Count) rows.Add(service.Rows[index]);
            }
        }
        _rows = rows;
        if (!HasMoreRows) IsExpanded = false;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasMoreRows));
        OnPropertyChanged(nameof(VisibleRows));
        RefreshLocalizedLabels();
    }

    internal void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(TotalLabel));
        OnPropertyChanged(nameof(ExpansionLabel));
    }
}
