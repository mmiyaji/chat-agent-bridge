using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace ChatAgentBridge_Desktop.ModelSelection;

public sealed partial class ModelCatalogMetadataControl : UserControl, INotifyPropertyChanged
{
    private DispatcherQueueTimer? _freshnessTimer;
    private string _metadataText = "";
    private bool _isRefreshRecommendedVisible;
    private string _refreshRecommendationTooltip = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public static readonly DependencyProperty CatalogProperty = DependencyProperty.Register(
        nameof(Catalog), typeof(ModelCatalogViewModel), typeof(ModelCatalogMetadataControl),
        new PropertyMetadata(null, OnCatalogChanged));

    public ModelCatalogViewModel? Catalog
    {
        get => (ModelCatalogViewModel?)GetValue(CatalogProperty);
        set => SetValue(CatalogProperty, value);
    }

    public string MetadataText
    {
        get => _metadataText;
        private set => SetProperty(ref _metadataText, value);
    }

    public bool IsRefreshRecommendedVisible
    {
        get => _isRefreshRecommendedVisible;
        private set => SetProperty(ref _isRefreshRecommendedVisible, value);
    }

    public string RefreshRecommendationTooltip
    {
        get => _refreshRecommendationTooltip;
        private set => SetProperty(ref _refreshRecommendationTooltip, value);
    }

    public ModelCatalogMetadataControl()
    {
        InitializeComponent();
        Loaded += Control_Loaded;
        Unloaded += Control_Unloaded;
        UpdateMetadata();
    }

    private static void OnCatalogChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var control = (ModelCatalogMetadataControl)dependencyObject;
        if (args.OldValue is ModelCatalogViewModel oldCatalog) oldCatalog.PropertyChanged -= control.Catalog_PropertyChanged;
        if (args.NewValue is ModelCatalogViewModel newCatalog) newCatalog.PropertyChanged += control.Catalog_PropertyChanged;
        control.UpdateMetadata();
    }

    private void Control_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshFreshness();
        _freshnessTimer ??= DispatcherQueue.GetForCurrentThread().CreateTimer();
        _freshnessTimer.Interval = TimeSpan.FromMinutes(30);
        _freshnessTimer.IsRepeating = true;
        _freshnessTimer.Tick -= FreshnessTimer_Tick;
        _freshnessTimer.Tick += FreshnessTimer_Tick;
        _freshnessTimer.Start();
        LanguageSettings.LanguageChanged -= LanguageChanged;
        LanguageSettings.LanguageChanged += LanguageChanged;
    }

    private void Control_Unloaded(object sender, RoutedEventArgs e)
    {
        _freshnessTimer?.Stop();
        LanguageSettings.LanguageChanged -= LanguageChanged;
    }

    private void FreshnessTimer_Tick(DispatcherQueueTimer sender, object args) => RefreshFreshness();

    private void LanguageChanged(CultureInfo culture) => RefreshFreshness();

    private void RefreshFreshness()
    {
        Catalog?.RefreshFreshness();
        UpdateMetadata();
    }

    private void Catalog_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModelCatalogViewModel.FetchedAtDisplayText)
            or nameof(ModelCatalogViewModel.HasCatalogMetadata)
            or nameof(ModelCatalogViewModel.IsRefreshRecommended))
        {
            UpdateMetadata();
        }
    }

    private void UpdateMetadata()
    {
        var catalog = Catalog;
        MetadataText = catalog?.HasCatalogMetadata == true ? catalog.FetchedAtDisplayText : "";
        IsRefreshRecommendedVisible = catalog?.HasCatalogMetadata == true && catalog.IsRefreshRecommended;
        RefreshRecommendationTooltip = DesktopText.Get("ModelCatalogRefreshRecommended");
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
