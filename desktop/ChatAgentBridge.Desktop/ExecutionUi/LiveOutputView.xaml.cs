using System.ComponentModel;
using Windows.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ChatAgentBridge_Desktop.ExecutionUi;

public sealed partial class LiveOutputView : UserControl
{
    public LiveOutputViewModel? ViewModel
    {
        get => (LiveOutputViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(LiveOutputViewModel), typeof(LiveOutputView), new PropertyMetadata(null, ViewModelChanged));

    private ScrollViewer? _outputScrollViewer;
    private int _selectionStart;
    private int _selectionLength;
    private int _lastRenderedTextLength = -1;
    private bool _restoringSelection;
    private bool _isLoaded;
    private bool _isInitialized;
    private bool _followScrollPending;

    public LiveOutputView()
    {
        InitializeComponent();
        AutomationProperties.SetLabeledBy(OutputTextBox, LiveOutputTitleText);
        _isInitialized = true;
        OutputExpander.RegisterPropertyChangedCallback(Expander.IsExpandedProperty, (sender, _) =>
        {
            if (sender is Expander { IsExpanded: true })
            {
                EnsureScrollViewer();
                ScrollToEndIfFollowing();
            }
        });
        Loaded += LiveOutputView_Loaded;
        Unloaded += LiveOutputView_Unloaded;
    }

    public static Visibility ShowWhen(bool first, bool second) => first || second ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility ShowWhenSingle(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility ShowWaiting(bool active, bool hasOutput) => active && !hasOutput ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility ShowWhenNot(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    private static void ViewModelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (LiveOutputView)sender;
        if (args.OldValue is LiveOutputViewModel oldViewModel)
            oldViewModel.PropertyChanged -= view.ViewModel_PropertyChanged;
        if (view._isLoaded && args.NewValue is LiveOutputViewModel newViewModel)
            newViewModel.PropertyChanged += view.ViewModel_PropertyChanged;
        view._selectionStart = 0;
        view._selectionLength = 0;
        if (view._isInitialized)
        {
            view._restoringSelection = true;
            view.OutputTextBox.Select(0, 0);
            view._restoringSelection = false;
        }
        view.ScrollToEndIfFollowing();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LiveOutputViewModel.FollowOutput) && ViewModel?.FollowOutput == true)
            ScrollToEndIfFollowing();
    }

    private void LiveOutputView_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        var hostAutomationId = AutomationProperties.GetAutomationId(this);
        if (!string.IsNullOrEmpty(hostAutomationId))
            AutomationProperties.SetAutomationId(OutputExpander, hostAutomationId);
        if (ViewModel is not null)
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        EnsureScrollViewer();
        ScrollToEndIfFollowing();
    }

    private void LiveOutputView_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        if (ViewModel is not null)
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        DetachScrollViewer();
        _outputScrollViewer = null;
    }

    private void OutputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        EnsureScrollViewer();
        var textLength = OutputTextBox.Text?.Length ?? 0;
        if (textLength == 0)
        {
            _selectionStart = 0;
            _selectionLength = 0;
        }
        _lastRenderedTextLength = textLength;
        var start = Math.Clamp(_selectionStart, 0, textLength);
        var length = Math.Clamp(_selectionLength, 0, textLength - start);
        _restoringSelection = true;
        OutputTextBox.Select(start, length);
        _restoringSelection = false;
        ScrollToEndIfFollowing();
    }

    private void OutputTextBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_restoringSelection)
            return;
        var selectionStart = OutputTextBox.SelectionStart;
        var selectionLength = OutputTextBox.SelectionLength;
        if (selectionLength == 0 && _selectionLength > 0 && OutputTextBox.Text.Length != _lastRenderedTextLength)
            return;
        _selectionStart = selectionStart;
        _selectionLength = selectionLength;
        if (!_restoringSelection && _selectionLength > 0 && ViewModel?.FollowOutput == true)
            ViewModel.FollowOutput = false;
    }

    private void FollowOutput_Checked(object sender, RoutedEventArgs e) => ScrollToEndIfFollowing();

    private void OutputTextBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Up or VirtualKey.PageUp or VirtualKey.Home)
            PauseFollowingIfAwayFromBottom();
    }

    private void EnsureScrollViewer()
    {
        if (_outputScrollViewer is not null)
            return;
        OutputExpander.ApplyTemplate();
        OutputTextBox.ApplyTemplate();
        _outputScrollViewer = FindScrollViewer(OutputTextBox);
        if (_outputScrollViewer is not null)
        {
            _outputScrollViewer.PointerWheelChanged += OutputScrollViewer_PointerWheelChanged;
            _outputScrollViewer.ManipulationStarted += OutputScrollViewer_ManipulationStarted;
        }
    }

    private void DetachScrollViewer()
    {
        if (_outputScrollViewer is not null)
        {
            _outputScrollViewer.PointerWheelChanged -= OutputScrollViewer_PointerWheelChanged;
            _outputScrollViewer.ManipulationStarted -= OutputScrollViewer_ManipulationStarted;
        }
    }

    private void OutputScrollViewer_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => DispatcherQueue.TryEnqueue(PauseFollowingIfAwayFromBottom);

    private void OutputScrollViewer_ManipulationStarted(object sender, Microsoft.UI.Xaml.Input.ManipulationStartedRoutedEventArgs e)
        => DispatcherQueue.TryEnqueue(PauseFollowingIfAwayFromBottom);

    private void PauseFollowingIfAwayFromBottom()
    {
        if (ViewModel?.FollowOutput == true && _outputScrollViewer is not null &&
            _outputScrollViewer.VerticalOffset < _outputScrollViewer.ScrollableHeight - 4)
            ViewModel.FollowOutput = false;
    }

    private void ScrollToEndIfFollowing()
    {
        if (ViewModel?.FollowOutput != true || _outputScrollViewer is null)
            return;

        if (_followScrollPending)
            return;

        // TextBox content extent changes after TextChanged. Defer until layout so the
        // new ScrollableHeight is available, then follow only if the user still follows.
        _followScrollPending = true;
        EventHandler<object>? layoutUpdated = null;
        layoutUpdated = (_, _) =>
        {
            LayoutUpdated -= layoutUpdated;
            _followScrollPending = false;
            if (ViewModel?.FollowOutput == true && _outputScrollViewer is not null)
                _outputScrollViewer.ChangeView(null, _outputScrollViewer.ScrollableHeight, null, true);
        };
        LayoutUpdated += layoutUpdated;
        OutputTextBox.UpdateLayout();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer scrollViewer)
                return scrollViewer;
            var descendant = FindScrollViewer(child);
            if (descendant is not null)
                return descendant;
        }
        return null;
    }
}
