using ChatAgentBridge.Core.AppServer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace ChatAgentBridge_Desktop.AppServerUi;

/// <summary>Shows fail-closed native dialogs for Codex AppServer callbacks.</summary>
public sealed class AppServerDialogs(Func<XamlRoot?> root)
{
    private static readonly SemaphoreSlim DialogGate = new(1, 1);
    private readonly Func<XamlRoot?> _root = root;

    public Task<AppServerDecision> ApproveAsync(AppServerApproval approval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        return ShowAsync(
            xamlRoot => BuildApprovalDialog(xamlRoot, approval),
            AppServerDecision.Decline,
            cancellationToken);
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
            Text = "Codex が次の操作の許可を求めています。内容を確認してください。",
            TextWrapping = TextWrapping.Wrap
        });

        var details = new StackPanel { Spacing = 8 };
        AddDetail(details, "メソッド", approval.Method);
        AddDetail(details, "スレッド", approval.ThreadId);
        AddDetail(details, "ターン", approval.TurnId);
        AddDetail(details, "項目", approval.ItemId);
        AddDetail(details, "説明", approval.Description);
        AddDetail(details, "コマンド", approval.Command);
        AddDetail(details, "作業フォルダー", approval.WorkingDirectory);
        var scroll = new ScrollViewer
        {
            Content = details,
            MaxHeight = 400,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        body.Children.Add(scroll);

        var dialog = new ContentDialog
        {
            Title = "操作の許可",
            Content = body,
            PrimaryButtonText = "許可",
            CloseButtonText = "拒否",
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
            Text = $"スレッド: {Display(input.ThreadId)}\nターン: {Display(input.TurnId)}\n項目: {Display(input.ItemId)}",
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
                    otherChoice = new RadioButton { Content = "その他", GroupName = $"AppServerQuestion{index}" };
                    choices.Add(otherChoice);
                    group.Children.Add(otherChoice);
                }
            }

            if (question.IsOther || question.Options.Count == 0)
            {
                textInput = question.IsSecret ? null : new TextBox
                {
                    PlaceholderText = "回答を入力",
                    Visibility = question.Options.Count > 0 ? Visibility.Collapsed : Visibility.Visible
                };
                passwordInput = question.IsSecret ? new PasswordBox
                {
                    PlaceholderText = "回答を入力",
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
            Title = "Codex への回答",
            Content = scroll,
            PrimaryButtonText = "送信",
            CloseButtonText = "キャンセル",
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
                $"{answer.Question.Question}: {answer.Choices[choiceIndex].Tag as string ?? "その他"}");
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
        if (string.IsNullOrEmpty(value)) return "(なし)";
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
