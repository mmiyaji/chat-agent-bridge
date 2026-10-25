using System.Collections.ObjectModel;
using System.Globalization;
using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatAgentBridge_Desktop.ModelSelection;

/// <summary>A successful lookup could not persist its optional diagnostic cache.</summary>
internal sealed class ModelCatalogPersistenceException(Exception innerException) : Exception(null, innerException);

/// <summary>State and cancellation for an explicit, user-requested model catalog lookup.</summary>
public sealed partial class ModelCatalogViewModel : ObservableObject, IDisposable
{
    private readonly ModelCatalogService _service = new();
    private CancellationTokenSource? _lookupCancellation;
    private Func<BridgeSettings> _settingsFactory;
    private readonly Action<ModelOption>? _selectionApplied;
    private readonly Func<BridgeSettings, IReadOnlyList<ModelOption>, DateTimeOffset, CancellationToken, Task>? _modelsFetched;
    private long _generation;
    private bool _disposed;
    private bool _restoringVerifiedModels;

    public ObservableCollection<ModelOption> Models { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchModelsCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelFetchCommand))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial ModelOption? SelectedModel { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string Status { get; set; } = DesktopText.Get("ModelCatalogAvailable");

    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    public DateTimeOffset? FetchedAtUtc { get; private set; }
    public bool HasModelsTimestamp => ModelCatalogFreshness.ValidFetchedAt(FetchedAtUtc, DateTimeOffset.UtcNow) is not null;
    public bool HasCatalogMetadata => Models.Count > 0;
    public bool IsRefreshRecommended => ModelCatalogFreshness.IsRefreshRecommended(Models.Count, FetchedAtUtc, DateTimeOffset.UtcNow);
    public string FetchedAtDisplayText => HasModelsTimestamp
        ? DesktopText.Get("ModelCatalogFetchedAt", FetchedAtUtc!.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
        : DesktopText.Get("ModelCatalogFetchedAtUnknown");

    public ModelCatalogViewModel(Func<BridgeSettings> settingsFactory, Action<ModelOption>? selectionApplied = null,
        Func<BridgeSettings, IReadOnlyList<ModelOption>, DateTimeOffset, CancellationToken, Task>? modelsFetched = null)
    {
        _settingsFactory = settingsFactory;
        _selectionApplied = selectionApplied;
        _modelsFetched = modelsFetched;
        Models.CollectionChanged += (_, _) => RefreshFreshness();
        PropertyChanged += (_, e) =>
        {
            if (!_restoringVerifiedModels && e.PropertyName == nameof(SelectedModel) && SelectedModel is { } selected)
                _selectionApplied?.Invoke(selected);
        };
    }

    public void UpdateSettingsFactory(Func<BridgeSettings> settingsFactory) => _settingsFactory = settingsFactory;

    /// <summary>Restores diagnostic candidates without launching the CLI.</summary>
    public void RestoreVerifiedModels(IReadOnlyList<ModelOption> models, string? selectedModel, DateTimeOffset? fetchedAtUtc = null)
    {
        _restoringVerifiedModels = true;
        try
        {
            Invalidate();
            foreach (var model in models) Models.Add(model);
            FetchedAtUtc = ModelCatalogFreshness.ValidFetchedAt(fetchedAtUtc, DateTimeOffset.UtcNow);
            RefreshFreshness();
            // Restoring diagnostic choices must not apply a model edit to settings.
            SelectedModel = Models.FirstOrDefault(model => string.Equals(model.Id, selectedModel?.Trim(), StringComparison.Ordinal));
            Status = DesktopText.Get("ModelCatalogRestored", Models.Count);
        }
        finally { _restoringVerifiedModels = false; }
    }

    public void CancelLookup()
    {
        if (!IsLoading) return;
        _generation++;
        CancelCurrentLookup();
        IsLoading = false;
        Status = DesktopText.Get("ModelCatalogCancelledLeaving");
    }

    public void Invalidate(string? message = null)
    {
        _generation++;
        CancelCurrentLookup();
        IsLoading = false;
        Models.Clear();
        FetchedAtUtc = null;
        RefreshFreshness();
        SelectedModel = null;
        Status = message ?? DesktopText.Get("ModelCatalogSettingsChanged");
    }

    [RelayCommand(CanExecute = nameof(CanFetch))]
    private async Task FetchModelsAsync()
    {
        if (_disposed) return;
        CancelCurrentLookup();
        var generation = ++_generation;
        var cancellation = new CancellationTokenSource();
        _lookupCancellation = cancellation;
        Status = DesktopText.Get("ModelCatalogLoading");
        IsLoading = true;
        try
        {
            var settings = _settingsFactory();
            if (!AgentProfile.HasModelCatalog(AgentProfile.ResolveProvider(settings)))
            {
                if (generation == _generation)
                    Status = DesktopText.Get("ModelCatalogUnsupported");
                return;
            }
            var models = await _service.GetModelsAsync(settings, cancellation.Token);
            if (generation != _generation || cancellation.IsCancellationRequested) return;
            var selectedId = SelectedModel?.Id;
            Models.Clear();
            foreach (var model in models) Models.Add(model);
            // Preserve a selection on refresh without applying another settings edit.
            _restoringVerifiedModels = true;
            try { SelectedModel = Models.FirstOrDefault(model => model.Id == selectedId); }
            finally { _restoringVerifiedModels = false; }
            var fetchedAtUtc = DateTimeOffset.UtcNow;
            FetchedAtUtc = fetchedAtUtc;
            RefreshFreshness();
            Status = models.Count == 0
                ? DesktopText.Get("ModelCatalogEmpty")
                : DesktopText.Get("ModelCatalogFound", models.Count);
            if (_modelsFetched is not null)
                await _modelsFetched(settings, models, fetchedAtUtc, cancellation.Token);
        }
        catch (ModelCatalogPersistenceException)
        {
            if (generation == _generation)
                Status += " " + DesktopText.Get("ModelCatalogSaveFailed");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (generation == _generation) Status = DesktopText.Get("ModelCatalogCancelled");
        }
        catch (TimeoutException)
        {
            if (generation == _generation) Status = DesktopText.Get("ModelCatalogTimedOut");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            if (generation == _generation) Status = DesktopText.Get("ModelCatalogLaunchFailed");
        }
        catch (UnauthorizedAccessException)
        {
            if (generation == _generation) Status = DesktopText.Get("ModelCatalogAccessDenied");
        }
        catch
        {
            if (generation == _generation) Status = DesktopText.Get("ModelCatalogFailed");
        }
        finally
        {
            if (generation == _generation)
            {
                IsLoading = false;
                _lookupCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    [RelayCommand(CanExecute = nameof(IsLoading))]
    private void CancelFetch() => CancelCurrentLookup();

    private bool CanFetch() => !_disposed && !IsLoading;

    /// <summary>Updates age guidance after time or display language changes without fetching models.</summary>
    public void RefreshFreshness()
    {
        OnPropertyChanged(nameof(FetchedAtUtc));
        OnPropertyChanged(nameof(HasModelsTimestamp));
        OnPropertyChanged(nameof(HasCatalogMetadata));
        OnPropertyChanged(nameof(IsRefreshRecommended));
        OnPropertyChanged(nameof(FetchedAtDisplayText));
    }

    private void CancelCurrentLookup()
    {
        var current = _lookupCancellation;
        _lookupCancellation = null;
        current?.Cancel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _generation++;
        CancelCurrentLookup();
    }
}
