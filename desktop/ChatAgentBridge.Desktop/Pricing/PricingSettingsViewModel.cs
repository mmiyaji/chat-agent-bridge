using System.Collections.ObjectModel;
using System.Globalization;
using ChatAgentBridge_Desktop.Localization;
using ChatAgentBridge.Core.Localization;
using ChatAgentBridge.Core.Pricing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatAgentBridge_Desktop.Pricing;

public sealed record PricingModelOption(string Model, string Display);

public sealed partial class PricingSettingsViewModel : ObservableObject
{
    private const double MaxRate = 7.9e28;
    private readonly PricingCatalogManager _manager;
    private bool _subscribed;
    private bool _updatingModelSelection;

    public ObservableCollection<PricingModelOption> ModelOptions { get; } = [];

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsRefreshing { get; set; }
    [ObservableProperty] public partial bool ManualFileHealthy { get; set; }
    [ObservableProperty] public partial bool IsCustomModel { get; set; }
    [ObservableProperty] public partial bool IsValid { get; set; }
    [ObservableProperty] public partial int SelectedModelIndex { get; set; }
    [ObservableProperty] public partial string ModelId { get; set; } = "";
    [ObservableProperty] public partial double InputMin { get; set; }
    [ObservableProperty] public partial double InputMax { get; set; }
    [ObservableProperty] public partial double CachedInputMin { get; set; }
    [ObservableProperty] public partial double CachedInputMax { get; set; }
    [ObservableProperty] public partial double CacheWriteMin { get; set; }
    [ObservableProperty] public partial double CacheWriteMax { get; set; }
    [ObservableProperty] public partial double OutputMin { get; set; }
    [ObservableProperty] public partial double OutputMax { get; set; }
    [ObservableProperty] public partial string ValidationText { get; set; } = "";
    [ObservableProperty] public partial string OperationErrorText { get; set; } = "";
    [ObservableProperty] public partial string ManagerErrorDetails { get; set; } = "";
    [ObservableProperty] public partial string LastCheckedText { get; set; } = "";
    [ObservableProperty] public partial string LastUpdatedText { get; set; } = "";
    [ObservableProperty] public partial string SourceText { get; set; } = "";
    [ObservableProperty] public partial string AutomaticBaselineText { get; set; } = "";
    [ObservableProperty] public partial string AutomaticFileText { get; set; } = "";
    [ObservableProperty] public partial string ManualFileText { get; set; } = "";
    [ObservableProperty] public partial string AutoUpdateStatusText { get; set; } = "";
    [ObservableProperty] public partial bool HasManagerError { get; set; }
    [ObservableProperty] public partial bool HasManagerErrorDetails { get; set; }
    [ObservableProperty] public partial bool HasOperationError { get; set; }
    [ObservableProperty] public partial bool HasManualOverride { get; set; }
    [ObservableProperty] public partial bool HasSelectedModel { get; set; }

    public PricingSettingsViewModel(PricingCatalogManager manager)
    {
        _manager = manager;
        ManualFileHealthy = manager.State.ManualFileHealthy;
        IsCustomModel = true;
        AutomaticFileText = manager.AutomaticFilePath;
        ManualFileText = manager.ManualFilePath;
        RefreshDisplay();
        RebuildModelOptions(null, updateRates: false);
        ValidateInput();
    }

    public bool AutoUpdateEnabled
    {
        get => _manager.State.AutoUpdateEnabled;
        set
        {
            if (IsBusy || value == _manager.State.AutoUpdateEnabled) return;
            _ = SetAutoUpdateAsync(value);
        }
    }

    public bool IsAutoUpdateEnabled => !IsBusy;
    public bool IsRefreshEnabled => !IsBusy;
    public bool IsEditingEnabled => !IsBusy && ManualFileHealthy;
    public bool CanRemoveManual => !IsBusy && ManualFileHealthy && HasManualOverride;
    public bool IsManagerHealthy => ManualFileHealthy && string.IsNullOrWhiteSpace(_manager.State.LastError);
    public string AutoUpdateHeader => DesktopText.Get("PricingAutoUpdateHeader");
    public string AutomaticBaselineHeading => DesktopText.Get("PricingAutomaticSource");
    public string RateMinimumHeader => DesktopText.Get("PricingRateMinimum");
    public string RateMaximumHeader => DesktopText.Get("PricingRateMaximum");
    public string SelectedModelName => TryGetSelectedModel(out var model) ? model : "";
    public string SelectedManualModelName => GetManualOverrideForSelection()?.Model ?? "";
    public string StatusText { get; private set; } = "";
    public string ManagerErrorText => DesktopText.Get(ManualFileHealthy ? "PricingUpdateErrorSummary" : "PricingManualFileCorrupt");
    public string PricingCheckedOn => _manager.Current.CheckedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public void Subscribe()
    {
        if (_subscribed) return;
        _manager.Changed += Manager_Changed;
        _subscribed = true;
        var selected = TryGetSelectedModel(out var model) ? model : null;
        RefreshDisplay();
        RebuildModelOptions(selected, updateRates: false);
    }

    public void Unsubscribe()
    {
        if (!_subscribed) return;
        _manager.Changed -= Manager_Changed;
        _subscribed = false;
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(AutoUpdateEnabled));
        OnPropertyChanged(nameof(IsAutoUpdateEnabled));
        OnPropertyChanged(nameof(IsRefreshEnabled));
        OnPropertyChanged(nameof(IsEditingEnabled));
        OnPropertyChanged(nameof(CanRemoveManual));
        RefreshCommands();
    }

    partial void OnManualFileHealthyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEditingEnabled));
        OnPropertyChanged(nameof(CanRemoveManual));
        OnPropertyChanged(nameof(ManagerErrorText));
        RefreshCommands();
    }

    partial void OnHasManualOverrideChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRemoveManual));
        RefreshCommands();
    }

    partial void OnSelectedModelIndexChanged(int value)
    {
        if (_updatingModelSelection) return;
        if (value <= 0 || value >= ModelOptions.Count)
        {
            IsCustomModel = true;
            ModelId = "";
            SetRates(null);
        }
        else
        {
            var option = ModelOptions[value];
            IsCustomModel = false;
            ModelId = option.Model;
            SetRates(Resolve(option.Model));
        }
        UpdateSelectedModelDisplay();
        ValidateInput();
    }

    partial void OnModelIdChanged(string value) { UpdateSelectedModelDisplay(); ValidateInput(); }
    partial void OnInputMinChanged(double value) => ValidateInput();
    partial void OnInputMaxChanged(double value) => ValidateInput();
    partial void OnCachedInputMinChanged(double value) => ValidateInput();
    partial void OnCachedInputMaxChanged(double value) => ValidateInput();
    partial void OnCacheWriteMinChanged(double value) => ValidateInput();
    partial void OnCacheWriteMaxChanged(double value) => ValidateInput();
    partial void OnOutputMinChanged(double value) => ValidateInput();
    partial void OnOutputMaxChanged(double value) => ValidateInput();

    private bool CanSaveManual() => !IsBusy && ManualFileHealthy && IsValid;
    private bool CanRemoveManualCommand() => CanRemoveManual;
    private bool CanRefreshPricing() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSaveManual))]
    private async Task SaveManualAsync()
    {
        if (!TryCreatePrice(out var price)) return;
        IsBusy = true;
        HasOperationError = false;
        try
        {
            await _manager.SaveManualAsync(price);
            OperationErrorText = "";
            RebuildModelOptions(price.Model, updateRates: true);
            RefreshDisplay();
        }
        catch (Exception ex) when (IsExpectedError(ex))
        {
            OperationErrorText = ex.Message;
            HasOperationError = true;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveManualCommand))]
    private async Task RemoveManualAsync()
    {
        if (!TryGetSelectedModel(out var selectedModel) || GetManualOverrideForSelection() is not { } manualPrice) return;
        IsBusy = true;
        HasOperationError = false;
        try
        {
            await _manager.RemoveManualAsync(manualPrice.Model);
            OperationErrorText = "";
            RefreshDisplay();
            SetRates(Resolve(selectedModel));
        }
        catch (Exception ex) when (IsExpectedError(ex))
        {
            OperationErrorText = ex.Message;
            HasOperationError = true;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRefreshPricing))]
    private async Task RefreshPricingAsync()
    {
        IsBusy = true;
        IsRefreshing = true;
        HasOperationError = false;
        try
        {
            await _manager.RefreshAsync(force: true);
            RefreshDisplay();
        }
        catch (Exception ex) when (IsExpectedError(ex))
        {
            OperationErrorText = ex.Message;
            HasOperationError = true;
        }
        finally { IsRefreshing = false; IsBusy = false; }
    }

    private async Task SetAutoUpdateAsync(bool enabled)
    {
        IsBusy = true;
        HasOperationError = false;
        try
        {
            await _manager.SetAutoUpdateAsync(enabled);
            if (enabled)
            {
                IsRefreshing = true;
                await _manager.RefreshAsync(force: false);
            }
        }
        catch (Exception ex) when (IsExpectedError(ex))
        {
            OperationErrorText = ex.Message;
            HasOperationError = true;
        }
        finally
        {
            IsRefreshing = false;
            IsBusy = false;
            OnPropertyChanged(nameof(AutoUpdateEnabled));
        }
    }

    private void Manager_Changed(object? sender, EventArgs e)
    {
        void Apply()
        {
            var selected = TryGetSelectedModel(out var model) ? model : null;
            RefreshDisplay();
            RebuildModelOptions(selected, updateRates: false);
        }

        if (App.DispatcherQueue is { } queue && !queue.HasThreadAccess)
            queue.TryEnqueue(Apply);
        else Apply();
    }

    private void RefreshDisplay()
    {
        var state = _manager.State;
        ManualFileHealthy = state.ManualFileHealthy;
        OnPropertyChanged(nameof(AutoUpdateEnabled));
        OnPropertyChanged(nameof(PricingCheckedOn));
        LastCheckedText = FormatTimestamp(state.LastCheckedUtc);
        LastUpdatedText = FormatTimestamp(state.LastUpdatedUtc);
        HasManagerError = !state.ManualFileHealthy || !string.IsNullOrWhiteSpace(state.LastError);
        HasManagerErrorDetails = !string.IsNullOrWhiteSpace(state.LastError);
        ManagerErrorDetails = state.LastError ?? "";
        OnPropertyChanged(nameof(ManagerErrorText));
        StatusText = IsManagerHealthy ? DesktopText.Get("PricingManagerHealthy") : "";
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsManagerHealthy));
        AutoUpdateStatusText = DesktopText.Get(state.AutoUpdateEnabled ? "PricingAutoUpdateEnabled" : "PricingAutoUpdateDisabled");
        AutomaticFileText = _manager.AutomaticFilePath;
        ManualFileText = _manager.ManualFilePath;
        UpdateSelectedModelDisplay();
    }

    private void RebuildModelOptions(string? preserveModel, bool updateRates)
    {
        _updatingModelSelection = true;
        int index;
        try
        {
            ModelOptions.Clear();
            ModelOptions.Add(new PricingModelOption("", DesktopText.Get("PricingModelSelectPrompt")));
            foreach (var item in _manager.Current.EffectivePrices.OrderBy(x => x.Model, StringComparer.Ordinal))
                ModelOptions.Add(new PricingModelOption(item.Model, item.Model));

            index = preserveModel is null ? 0 : ModelOptions.ToList().FindIndex(x => x.Model == preserveModel);
            SelectedModelIndex = index > 0 ? index : 0;
        }
        finally { _updatingModelSelection = false; }
        if (preserveModel is null)
        {
            IsCustomModel = true;
            ModelId = "";
            if (updateRates) SetRates(null);
        }
        else if (index > 0)
        {
            IsCustomModel = false;
            ModelId = preserveModel;
            if (updateRates) SetRates(Resolve(preserveModel));
        }
        else
        {
            IsCustomModel = true;
            ModelId = preserveModel;
            if (updateRates) SetRates(Resolve(preserveModel));
        }
        UpdateSelectedModelDisplay();
    }

    private ModelPrice? Resolve(string model) => _manager.Current.TryResolve(model, out var resolved) ? resolved : null;

    private void UpdateSelectedModelDisplay()
    {
        OnPropertyChanged(nameof(SelectedModelName));
        OnPropertyChanged(nameof(SelectedManualModelName));
        HasSelectedModel = !string.IsNullOrWhiteSpace(ModelId);
        if (!HasSelectedModel)
        {
            HasManualOverride = false;
            SourceText = "";
            AutomaticBaselineText = "";
            return;
        }

        var current = _manager.Current;
        var manualSnapshot = new PricingSnapshot(current.CheckedOn, [], current.ManualPrices);
        var automaticSnapshot = new PricingSnapshot(current.CheckedOn, current.AutomaticPrices, []);
        var manual = manualSnapshot.TryResolve(ModelId, out var manualPrice) ? manualPrice : null;
        var automatic = automaticSnapshot.TryResolve(ModelId, out var automaticPrice) ? automaticPrice : null;
        HasManualOverride = manual is not null;
        SourceText = HasManualOverride
            ? DesktopText.Get("PricingManualSource")
            : automatic is not null ? DesktopText.Get("PricingAutomaticSource") : DesktopText.Get("PricingNoAutomaticPrice");
        AutomaticBaselineText = automatic is null ? DesktopText.Get("PricingNoAutomaticPrice") : FormatPrice(automatic);
    }

    private ModelPrice? GetManualOverrideForSelection()
    {
        if (!TryGetSelectedModel(out var model)) return null;
        var current = _manager.Current;
        var manualSnapshot = new PricingSnapshot(current.CheckedOn, [], current.ManualPrices);
        return manualSnapshot.TryResolve(model, out var price) ? price : null;
    }

    private void SetRates(ModelPrice? price)
    {
        InputMin = (double)(price?.Input.Min ?? 0m);
        InputMax = (double)(price?.Input.Max ?? 0m);
        CachedInputMin = (double)(price?.CachedInput.Min ?? 0m);
        CachedInputMax = (double)(price?.CachedInput.Max ?? 0m);
        CacheWriteMin = (double)(price?.CacheWrite.Min ?? 0m);
        CacheWriteMax = (double)(price?.CacheWrite.Max ?? 0m);
        OutputMin = (double)(price?.Output.Min ?? 0m);
        OutputMax = (double)(price?.Output.Max ?? 0m);
    }

    private void ValidateInput()
    {
        var id = ModelId.Trim();
        var validId = id.Length is > 0 and <= 200 && !id.Any(char.IsControl);
        var rates = new[] { (InputMin, InputMax), (CachedInputMin, CachedInputMax),
            (CacheWriteMin, CacheWriteMax), (OutputMin, OutputMax) };
        var finiteNonnegative = rates.All(x => double.IsFinite(x.Item1) && double.IsFinite(x.Item2) && x.Item1 >= 0 && x.Item2 >= 0 && x.Item1 <= MaxRate && x.Item2 <= MaxRate);
        var ordered = finiteNonnegative && rates.All(x => x.Item1 <= x.Item2);
        IsValid = validId && ordered;
        ValidationText = !validId ? DesktopText.Get("PricingValidationModel") :
            !finiteNonnegative ? DesktopText.Get("PricingValidationRate") :
            !ordered ? DesktopText.Get("PricingValidationRange") : "";
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        SaveManualCommand.NotifyCanExecuteChanged();
        RemoveManualCommand.NotifyCanExecuteChanged();
        RefreshPricingCommand.NotifyCanExecuteChanged();
    }

    private bool TryCreatePrice(out ModelPrice price)
    {
        price = null!;
        if (!IsValid || !TryGetSelectedModel(out var model)) return false;
        try
        {
            price = new ModelPrice(model, Rate(InputMin, InputMax), Rate(CachedInputMin, CachedInputMax),
                Rate(CacheWriteMin, CacheWriteMax), Rate(OutputMin, OutputMax));
            return true;
        }
        catch (OverflowException)
        {
            ValidationText = DesktopText.Get("PricingValidationRate");
            IsValid = false;
            return false;
        }
    }

    private bool TryGetSelectedModel(out string model)
    {
        model = IsCustomModel ? ModelId.Trim() : SelectedModelIndex > 0 && SelectedModelIndex < ModelOptions.Count ? ModelOptions[SelectedModelIndex].Model : "";
        return !string.IsNullOrWhiteSpace(model);
    }

    private static bool IsExpectedError(Exception ex) => ex is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or OperationCanceledException;
    private static bool ValidRate(double min, double max) => double.IsFinite(min) && double.IsFinite(max) && min >= 0 && max >= min && max <= MaxRate;
    private static PricingRate Rate(double min, double max) => new(checked((decimal)min), checked((decimal)max));

    private static string FormatPrice(ModelPrice price) => string.Join(Environment.NewLine,
        FormatRate("PricingInput", price.Input), FormatRate("PricingCachedInput", price.CachedInput),
        FormatRate("PricingCacheWrite", price.CacheWrite), FormatRate("PricingOutput", price.Output));

    private static string FormatRate(string labelKey, PricingRate rate) =>
        DesktopText.Get("PricingRateValue", DesktopText.Get(labelKey), Usd(rate.Min), Usd(rate.Max));

    private static string Usd(decimal amount) => "$" + amount.ToString("0.######", CultureInfo.CurrentCulture);

    private static string FormatTimestamp(DateTimeOffset? value) => value?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? DesktopText.Get("PricingNever");
}
