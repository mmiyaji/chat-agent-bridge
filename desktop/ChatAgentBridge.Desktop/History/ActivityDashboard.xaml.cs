using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace ChatAgentBridge_Desktop.History;

public sealed partial class ActivityDashboard : UserControl
{
    private bool? _narrow;
    public HistoryPageViewModel ViewModel { get; }
    public IReadOnlyList<string> WeekdayLabels { get; } = Enumerable.Range(0, 7)
        .Select(day => CultureInfo.CurrentUICulture.DateTimeFormat.GetShortestDayName((DayOfWeek)((day + 1) % 7))).ToArray();
    public event Action? OpenDayRequested;

    public ActivityDashboard(HistoryPageViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ActivityDailyViewport.SizeChanged += (_, e) => FitDailyBars(ActivityDailyPlot, e.NewSize.Width);
        UsageDailyViewport.SizeChanged += (_, e) => FitDailyBars(UsageDailyPlot, e.NewSize.Width);
        SizeChanged += (_, _) => UpdateLayoutForWidth();
        Loaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnViewModelChanged;
            ViewModel.PropertyChanged += OnViewModelChanged;
            ScrollToSelectedDay();
        };
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelChanged;
    }

    public static Style HeatStyle(int level) => (Style)Application.Current.Resources[
        level < 0 ? "ActivityHeatOutside" : $"ActivityHeat{level}"];

    // Both daily charts have 28 bounded cells. Explicit widths avoid the effective-viewport
    // measure loop of a virtualizing repeater inside this scrolling Pivot page.
    private static void FitDailyBars(ItemsControl chart, double width)
    {
        var cellWidth = Math.Max(8, Math.Floor(width / 28));
        if (chart.ItemContainerStyle?.Setters.FirstOrDefault() is Setter { Value: double current } && current == cellWidth) return;
        var style = new Style(typeof(ContentPresenter));
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, cellWidth));
        style.Setters.Add(new Setter(ContentPresenter.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        chart.ItemContainerStyle = style;
    }

    private void CalendarGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CalendarGrid.SelectedItem is ActivityCalendarCell day)
            ViewModel.SelectedActivityDay = day;
    }

    private void CalendarGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not ActivityCalendarCell day) return;
        args.ItemContainer.IsEnabled = true;
        AutomationProperties.SetAutomationId(args.ItemContainer, $"ActivityDay_{day.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}");
    }

    private void OpenDay_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CanOpenActivityDay) OpenDayRequested?.Invoke();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.ActivityRange)) ScrollToSelectedDay();
    }

    private void ScrollToSelectedDay() => DispatcherQueue.TryEnqueue(() =>
    {
        if (ViewModel.SelectedActivityDay is { } day) CalendarGrid.ScrollIntoView(day);
    });

    private void UpdateLayoutForWidth()
    {
        var narrow = ActualWidth < 680;
        if (_narrow == narrow) return;
        _narrow = narrow;
        Grid.SetColumn(StreakCard, narrow ? 0 : 2);
        Grid.SetColumn(PeakCard, narrow ? 1 : 3);
        Grid.SetRow(StreakCard, narrow ? 1 : 0);
        Grid.SetRow(PeakCard, narrow ? 1 : 0);
        SummaryGrid.ColumnDefinitions[2].Width = new GridLength(narrow ? 0 : 1, GridUnitType.Star);
        SummaryGrid.ColumnDefinitions[3].Width = new GridLength(narrow ? 0 : 1, GridUnitType.Star);
        Grid.SetColumn(ModelCard, narrow ? 0 : 1);
        Grid.SetRow(ModelCard, narrow ? 1 : 0);
        Grid.SetColumnSpan(WeekdayCard, narrow ? 2 : 1);
        Grid.SetColumnSpan(ModelCard, narrow ? 2 : 1);
        Grid.SetColumn(UsageScopeControl, narrow ? 0 : 1);
        Grid.SetRow(UsageScopeControl, narrow ? 1 : 0);
        Grid.SetColumnSpan(UsageMetricControl, narrow ? 2 : 1);
        Grid.SetColumnSpan(UsageScopeControl, narrow ? 2 : 1);
    }
}
