using ChatAgentBridge.Core.AppServer;
using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace ChatAgentBridge_Desktop.AppServerUi;

/// <summary>Shows fail-closed native dialogs for Codex AppServer callbacks.</summary>
public sealed class AppServerDialogs(Func<XamlRoot?> root)
{
    private static readonly SemaphoreSlim DialogGate = new(1, 1);
    private readonly Func<XamlRoot?> _root = root;

    public async Task<AppServerDecision> ApproveAsync(AppServerApproval approval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        using var localCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var quickDecision = new TaskCompletionSource<AppServerDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        string token = "";
        try
        {
            await OnUiAsync(() =>
            {
                App.PendingRequestNotifications.TryNotifyPendingRequest(Notifications.PendingNotificationKind.AiApproval,
                    () => { quickDecision.TrySetResult(AppServerDecision.Accept); return Task.CompletedTask; },
                    () => { quickDecision.TrySetResult(AppServerDecision.Decline); return Task.CompletedTask; },
                    () => { App.Window.Activate(); return Task.CompletedTask; }, out token);
            });
            var local = ShowAsync(xamlRoot => BuildApprovalDialog(xamlRoot, approval),
                AppServerDecision.Decline, localCancellation.Token);
            var winner = await Task.WhenAny(local, quickDecision.Task).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) return AppServerDecision.Decline;
            return await (winner == quickDecision.Task ? quickDecision.Task : local).ConfigureAwait(false);
        }
        finally
        {
            localCancellation.Cancel();
            await OnUiAsync(() => App.PendingRequestNotifications.Resolve(token));
        }
    }

    private static Task OnUiAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!App.DispatcherQueue.TryEnqueue(() =>
        {
            try { action(); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) completion.TrySetCanceled();
        return completion.Task;
    }

    public Task<IReadOnlyDictionary<string, string[]>?> InputAsync(AppServerInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!IsSupported(input))
            return Task.FromResult<IReadOnlyDictionary<string, string[]>?>(null);

        return ShowAsync(
            xamlRoot => BuildInputDialog(xamlRoot, input),
            (IReadOnlyDictionary<string, string[]>?)null,
            cancellationToken);
    }

    private async Task<TResult> ShowAsync<TResult>(
        Func<XamlRoot, (ContentDialog Dialog, Func<TResult> ReadAccepted)> build,
        TResult fallback,
        CancellationToken cancellationToken)
    {
        try
        {
            await DialogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return fallback;
        }

        try
        {
            if (cancellationToken.IsCancellationRequested)
                return fallback;

            var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (App.DispatcherQueue is null || !App.DispatcherQueue.TryEnqueue(async () =>
                {
                    ContentDialog? dialog = null;
                    CancellationTokenRegistration registration = default;
                    try
                    {
                        if (cancellationToken.IsCancellationRequested || _root() is not { } xamlRoot)
                        {
                            completion.TrySetResult(fallback);
                            return;
                        }

                        (dialog, Func<TResult> readAccepted) = build(xamlRoot);
                        registration = cancellationToken.Register(() =>
                        {
                            if (!App.DispatcherQueue.TryEnqueue(() =>
                                {
                                    try { dialog.Hide(); }
                                    catch { completion.TrySetResult(fallback); }
                                }))
                            {
                                completion.TrySetResult(fallback);
                            }
                        });

                        ContentDialogResult result = await dialog.ShowAsync();
                        if (cancellationToken.IsCancellationRequested || result != ContentDialogResult.Primary)
                            completion.TrySetResult(fallback);
                        else
                            completion.TrySetResult(readAccepted());
                    }
                    catch
                    {
                        completion.TrySetResult(fallback);
                    }
                    finally
                    {
                        registration.Dispose();
                    }
                }))
            {
                completion.TrySetResult(fallback);
            }

            return await completion.Task.ConfigureAwait(false);
        }
        catch
        {
            return fallback;
        }
        finally
        {
            DialogGate.Release();
        }
    }

    private static (ContentDialog Dialog, Func<AppServerDecision> ReadAccepted) BuildApprovalDialog(
        XamlRoot xamlRoot,
        AppServerApproval approval)
    {
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock
        {
            Text = DesktopText.Get("AppServerApprovalPrompt"),
            TextWrapping = TextWrapping.Wrap
        });

        var details = new StackPanel { Spacing = 8 };
        AddDetail(details, DesktopText.Get("Method"), approval.Method);
        AddDetail(details, DesktopText.Get("Thread"), approval.ThreadId);
        AddDetail(details, DesktopText.Get("Turn"), approval.TurnId);
        AddDetail(details, DesktopText.Get("Item"), approval.ItemId);
        AddDetail(details, DesktopText.Get("Description"), approval.Description);
        AddDetail(details, DesktopText.Get("Command"), approval.Command);
        AddDetail(details, DesktopText.Get("WorkingFolder"), approval.WorkingDirectory);
        var scroll = new ScrollViewer
        {
            Content = details,
            MaxHeight = 400,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        body.Children.Add(scroll);

        var dialog = new ContentDialog
        {
            Title = DesktopText.Get("AllowOperation"),
            Content = body,
            PrimaryButtonText = DesktopText.Get("Allow"),
            CloseButtonText = DesktopText.Get("Deny"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot
        };
        AutomationProperties.SetAutomationId(dialog, "AppServerApprovalDialog");
        return (dialog, () => AppServerDecision.Accept);
    }

    private static (ContentDialog Dialog, Func<IReadOnlyDictionary<string, string[]>?> ReadAccepted) BuildInputDialog(
        XamlRoot xamlRoot,
        AppServerInput input)
    {
        var answers = new List<InputAnswer>();
        var body = new StackPanel { Spacing = 12 };
        var metadata = new TextBlock
        {
            Text = DesktopText.Get("InputMetadata", Display(input.ThreadId), Display(input.TurnId), Display(input.ItemId)),
            TextWrapping = TextWrapping.Wrap
        };
        body.Children.Add(metadata);

        for (var index = 0; index < input.Questions.Count; index++)
        {
            AppServerQuestion question = input.Questions[index];
            var group = new StackPanel { Spacing = 8 };
            group.Children.Add(new TextBlock
            {
                Text = Display(question.Header),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            group.Children.Add(new TextBlock { Text = Display(question.Question), TextWrapping = TextWrapping.Wrap });

            var choices = new List<RadioButton>();
            RadioButton? otherChoice = null;
            TextBox? textInput = null;
            PasswordBox? passwordInput = null;
            if (question.Options.Count > 0)
            {
                foreach (AppServerInputOption option in question.Options)
                {
                    var choice = new RadioButton
                    {
                        GroupName = $"AppServerQuestion{index}",
                        Tag = option.Label,
                        Content = OptionContent(option)
                    };
                    choices.Add(choice);
                    group.Children.Add(choice);
                }
                if (question.IsOther)
                {
                    otherChoice = new RadioButton { Content = DesktopText.Get("Other"), GroupName = $"AppServerQuestion{index}" };
                    choices.Add(otherChoice);
                    group.Children.Add(otherChoice);
                }
            }

            if (question.IsOther || question.Options.Count == 0)
            {
                textInput = question.IsSecret ? null : new TextBox
                {
                    PlaceholderText = DesktopText.Get("EnterAnswer"),
                    Visibility = question.Options.Count > 0 ? Visibility.Collapsed : Visibility.Visible
                };
                passwordInput = question.IsSecret ? new PasswordBox
                {
                    PlaceholderText = DesktopText.Get("EnterAnswer"),
                    Visibility = question.Options.Count > 0 ? Visibility.Collapsed : Visibility.Visible
                } : null;
                if (textInput is not null) group.Children.Add(textInput);
                if (passwordInput is not null) group.Children.Add(passwordInput);

                if (otherChoice is not null)
                {
                    foreach (RadioButton choice in choices)
                    {
                        choice.Checked += (_, _) =>
                        {
                            Visibility visibility = ReferenceEquals(choice, otherChoice) ? Visibility.Visible : Visibility.Collapsed;
                            if (textInput is not null) textInput.Visibility = visibility;
                            if (passwordInput is not null) passwordInput.Visibility = visibility;
                        };
                    }
                }
            }

            var answer = new InputAnswer(question, choices, otherChoice, textInput, passwordInput);
            answers.Add(answer);
            group.Tag = answer;
            body.Children.Add(group);
        }

        var scroll = new ScrollViewer
        {
            Content = body,
            MaxHeight = 400,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var dialog = new ContentDialog
        {
            Title = DesktopText.Get("AnswerToCodex"),
            Content = scroll,
            PrimaryButtonText = DesktopText.Get("Send"),
            CloseButtonText = DesktopText.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            XamlRoot = xamlRoot
        };
        AutomationProperties.SetAutomationId(dialog, "AppServerInputDialog");

        void UpdateSubmitState() => dialog.IsPrimaryButtonEnabled = answers.All(answer => answer.HasValue);
        for (var index = 0; index < answers.Count; index++)
        {
            InputAnswer answer = answers[index];
            foreach (RadioButton choice in answer.Choices)
            {
                choice.Checked += (_, _) => UpdateSubmitState();
                choice.Unchecked += (_, _) => UpdateSubmitState();
            }
            if (answer.TextInput is not null) answer.TextInput.TextChanged += (_, _) => UpdateSubmitState();
            if (answer.PasswordInput is not null) answer.PasswordInput.PasswordChanged += (_, _) => UpdateSubmitState();
            SetQuestionAutomationIds(answer, index + 1);
        }
        dialog.Loaded += (_, _) => UpdateSubmitState();

        IReadOnlyDictionary<string, string[]>? ReadAnswers()
        {
            if (answers.Any(answer => !answer.HasValue)) return null;
            return answers.ToDictionary(answer => answer.Question.Id, answer => new[] { answer.Value! });
        }

        return (dialog, ReadAnswers);
    }

    private static void SetQuestionAutomationIds(InputAnswer answer, int index)
    {
        for (var choiceIndex = 0; choiceIndex < answer.Choices.Count; choiceIndex++)
        {
            AutomationProperties.SetAutomationId(answer.Choices[choiceIndex], $"AppServerInputChoice{index}_{choiceIndex + 1}");
            AutomationProperties.SetName(answer.Choices[choiceIndex],
                $"{answer.Question.Question}: {answer.Choices[choiceIndex].Tag as string ?? DesktopText.Get("Other")}");
        }
        if (answer.TextInput is not null)
        {
            AutomationProperties.SetAutomationId(answer.TextInput, $"AppServerInputAnswer{index}");
            AutomationProperties.SetName(answer.TextInput, answer.Question.Question);
        }
        if (answer.PasswordInput is not null)
        {
            AutomationProperties.SetAutomationId(answer.PasswordInput, $"AppServerInputAnswer{index}");
            AutomationProperties.SetName(answer.PasswordInput, answer.Question.Question);
        }
    }

    private static bool IsSupported(AppServerInput input)
    {
        if (input.Questions is null || input.Questions.Count is < 1 or > 10) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (AppServerQuestion question in input.Questions)
        {
            if (question is null || string.IsNullOrWhiteSpace(question.Id) || question.Id.Length > 256 || !ids.Add(question.Id) ||
                question.Header is null || question.Header.Length > 256 || question.Question is null || question.Question.Length > 4096 ||
                question.Options is null || question.Options.Count > 50 || (question.Options.Count == 0 && !question.IsOther))
                return false;

            foreach (AppServerInputOption option in question.Options)
            {
                if (option is null || string.IsNullOrWhiteSpace(option.Label) || option.Label.Length > 256 ||
                    option.Description is null || option.Description.Length > 2048)
                    return false;
            }
        }
        return true;
    }

    private static UIElement OptionContent(AppServerInputOption option)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock { Text = Display(option.Label), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(option.Description))
            stack.Children.Add(new TextBlock { Text = Display(option.Description), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        return stack;
    }

    private static void AddDetail(Panel panel, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock { Text = label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text = Display(value), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(stack);
    }

    private static string Display(string? value)
    {
        if (string.IsNullOrEmpty(value)) return DesktopText.Get("None");
        const int maximum = 8000;
        return value.Length <= maximum ? value : value[..maximum] + "…";
    }

    private sealed record InputAnswer(
        AppServerQuestion Question,
        IReadOnlyList<RadioButton> Choices,
        RadioButton? OtherChoice,
        TextBox? TextInput,
        PasswordBox? PasswordInput)
    {
        public bool HasValue
        {
            get
            {
                if (Choices.Count > 0)
                {
                    RadioButton? selected = Choices.FirstOrDefault(choice => choice.IsChecked == true);
                    if (selected is null) return false;
                    if (!ReferenceEquals(selected, OtherChoice)) return true;
                }
                return !string.IsNullOrWhiteSpace(TextInput?.Text ?? PasswordInput?.Password);
            }
        }

        public string? Value
        {
            get
            {
                RadioButton? selected = Choices.FirstOrDefault(choice => choice.IsChecked == true);
                if (selected is not null && !ReferenceEquals(selected, OtherChoice))
                    return selected.Tag as string;
                return TextInput?.Text ?? PasswordInput?.Password;
            }
        }
    }
}
