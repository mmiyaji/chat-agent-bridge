using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace ChatAgentBridge_Desktop.History;

public sealed partial class ActivityDashboard : UserControl
{
    public HistoryPageViewModel ViewModel { get; }
    public IReadOnlyList<string> WeekdayLabels { get; } = Enumerable.Range(0, 7)
        .Select(day => CultureInfo.CurrentUICulture.DateTimeFormat.GetShortestDayName((DayOfWeek)((day + 1) % 7))).ToArray();
    public event Action? OpenDayRequested;

    public ActivityDashboard(HistoryPageViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
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

    private void CalendarGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CalendarGrid.SelectedItem is ActivityCalendarCell { InRange: true } day)
            ViewModel.SelectedActivityDay = day;
    }

    private void CalendarGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not ActivityCalendarCell day) return;
        args.ItemContainer.IsEnabled = day.InRange;
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
    }
}
