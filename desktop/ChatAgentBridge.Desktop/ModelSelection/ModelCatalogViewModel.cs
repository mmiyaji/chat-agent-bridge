using System.Collections.ObjectModel;
using ChatAgentBridge.Core;
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
    public partial string Status { get; set; } = "必要なときに候補を取得できます。CLIの候補です。利用権限や料金は保証されません。";

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
        Status = "画面を離れたため、モデル候補の取得をキャンセルしました。";
    }

    public void Invalidate(string message = "設定が変わりました。候補をもう一度取得してください。")
    {
        _generation++;
        CancelCurrentLookup();
        IsLoading = false;
        Models.Clear();
        SelectedModel = null;
        Status = message;
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
        Status = "CLIからモデル候補を取得しています…";
        IsLoading = true;
        try
        {
            var settings = _settingsFactory();
            if (string.Equals(settings.AgentProvider, "custom", StringComparison.OrdinalIgnoreCase))
            {
                if (generation == _generation)
                    Status = "このCLIでは候補を取得できません。モデル名を直接入力してください。";
                return;
            }
            var models = await _service.GetModelsAsync(settings, cancellation.Token);
            if (generation != _generation || cancellation.IsCancellationRequested) return;
            foreach (var model in models) Models.Add(model);
            Status = models.Count == 0
                ? "モデル候補はありませんでした。モデル名を直接入力できます。"
                : $"{models.Count} 件の候補があります。選択するとモデル名に反映されます。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (generation == _generation) Status = "モデル候補の取得をキャンセルしました。";
        }
        catch
        {
            if (generation == _generation) Status = "候補を取得できませんでした。CLIの設定を確認するか、モデル名を直接入力してください。";
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
