using System.Collections.ObjectModel;
using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatAgentBridge_Desktop.ModelSelection;

/// <summary>State and cancellation for an explicit, user-requested model catalog lookup.</summary>
public sealed partial class ModelCatalogViewModel : ObservableObject, IDisposable
{
    private readonly ModelCatalogService _service = new();
    private CancellationTokenSource? _lookupCancellation;
    private Func<BridgeSettings> _settingsFactory;
    private readonly Action<ModelOption>? _selectionApplied;
    private long _generation;
    private bool _disposed;

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

    public ModelCatalogViewModel(Func<BridgeSettings> settingsFactory, Action<ModelOption>? selectionApplied = null)
    {
        _settingsFactory = settingsFactory;
        _selectionApplied = selectionApplied;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SelectedModel) && SelectedModel is { } selected)
                _selectionApplied?.Invoke(selected);
        };
    }

    public void UpdateSettingsFactory(Func<BridgeSettings> settingsFactory) => _settingsFactory = settingsFactory;

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
        Models.Clear();
        SelectedModel = null;
        Status = DesktopText.Get("ModelCatalogLoading");
        IsLoading = true;
        try
        {
            var settings = _settingsFactory();
            if (AgentProfile.ResolveProvider(settings) is not (AgentProfile.Codex or AgentProfile.Claude))
            {
                if (generation == _generation)
                    Status = DesktopText.Get("ModelCatalogUnsupported");
                return;
            }
            var models = await _service.GetModelsAsync(settings, cancellation.Token);
            if (generation != _generation || cancellation.IsCancellationRequested) return;
            foreach (var model in models) Models.Add(model);
            Status = models.Count == 0
                ? DesktopText.Get("ModelCatalogEmpty")
                : DesktopText.Get("ModelCatalogFound", models.Count);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (generation == _generation) Status = DesktopText.Get("ModelCatalogCancelled");
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
