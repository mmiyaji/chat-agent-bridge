using ChatAgentBridge.Core;
using ChatAgentBridge.Core.AppServer;
using ChatAgentBridge.Transports;

namespace ChatAgentBridge.Transports.Tests;

public sealed class ChatInputBrokerTests
{
    private const string OriginId = "server-origin-hash";
    private static readonly ChatRequest Origin = new("mattermost", "channel-1", "thread-1", "user-1", "run", "message-1");
    private static readonly AppServerInput Input = new("thread-1", "turn-1", "item-1",
    [
        new("private-option-id", "Choice", "Choose one", false, false,
            [new("First", "first choice"), new("Second", "second choice")]),
        new("private-text-id", "Details", "Add context", true, false, [])
    ]);

    [Fact]
    public async Task PublishedQuestionsMapNumberedOptionsAndFreeTextToPrivateIds()
    {
        var transport = new FakeTransport();
        AppServerInput? formattedInput = null;
        var broker = new ChatInputBroker((input, code) =>
        {
            formattedInput = input;
            return $"Questions code {code}: {input.Questions.Count}";
        });
        int authorizationChecks = 0;
        Task<IReadOnlyDictionary<string, string[]>?> request = broker.RequestAsync(
            transport, Origin, OriginId, Input, _ => { authorizationChecks++; return true; }, null,
            publishToThread: true);
        string code = GetCode(await transport.Reply.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.True(broker.TryHandle(Origin with { Text = $"answer {code} 1 2" }, OriginId));
        Assert.True(broker.TryHandle(Origin with { Text = $"answer {code} 2 My   free text" }, OriginId));
        var answers = await request.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(answers);
        Assert.Equal(new[] { "Second" }, answers["private-option-id"]);
        Assert.Equal(new[] { "My   free text" }, answers["private-text-id"]);
        Assert.Equal(4, authorizationChecks);
        Assert.Equal(new[] { "1", "2" }, formattedInput!.Questions.Select(question => question.Id));
        Assert.Empty(formattedInput.ThreadId);
        Assert.Empty(formattedInput.TurnId);
        Assert.Empty(formattedInput.ItemId);
        Assert.DoesNotContain("private-option-id", transport.ReplyText);
        Assert.DoesNotContain("private-text-id", transport.ReplyText);
        Assert.True(broker.TryHandle(Origin with { Text = $"answer {code} 1 1" }, OriginId));
    }

    [Fact]
    public async Task SecretQuestionsAreAlwaysLocalEvenWhenSharingIsEnabled()
    {
        var secretInput = new AppServerInput("thread-1", "turn-1", "item-1",
            [new("secret-id", "Password", "Enter a credential", false, true, [])]);
        var transport = new FakeTransport();
        var broker = CreateBroker();
        IReadOnlyDictionary<string, string[]>? localAnswers = null;

        var result = await broker.RequestAsync(transport, Origin, OriginId, secretInput, _ => true,
            (_, _) => Task.FromResult<IReadOnlyDictionary<string, string[]>?>(
                localAnswers = new Dictionary<string, string[]> { ["secret-id"] = ["local value"] }),
            publishToThread: true);

        Assert.Same(localAnswers, result);
        Assert.False(transport.Reply.Task.IsCompleted);
    }

    [Fact]
    public async Task WrongScopeUnauthorizedAndDuplicateAnswersAreConsumedWithoutTakingOwnership()
    {
        var transport = new FakeTransport();
        var broker = CreateBroker();
        var authorized = true;
        var task = broker.RequestAsync(transport, Origin, OriginId, Input,
            _ => authorized, null, publishToThread: true);
        string code = GetCode(await transport.Reply.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.True(broker.TryHandle(Origin with { Text = $"answer {code} 1 1", UserId = "other-user" }, OriginId));
        Assert.True(broker.TryHandle(Origin with { Text = $"answer {code} 1 1" }, "different-origin"));
        authorized = false;
        Assert.True(broker.TryHandle(Origin with { Text = $"answer {code} 1 1" }, OriginId));
        Assert.False(task.IsCompleted);

        authorized = true;
        Assert.True(broker.TryHandle(Origin with { Text = $"answer {code} 1 1" }, OriginId));
        Assert.True(broker.TryHandle(Origin with { Text = $"answer {code} 1 2" }, OriginId));
        Assert.True(broker.TryHandle(Origin with { Text = $"answer {code} 2 other Additional detail" }, OriginId));
        var answers = await task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(answers);
        Assert.Equal(new[] { "First" }, answers["private-option-id"]);
        Assert.Equal(new[] { "Additional detail" }, answers["private-text-id"]);
    }

    [Fact]
    public async Task InvalidOptionAndMalformedOrStaleAnswerNeverBecomeARequestResult()
    {
        var transport = new FakeTransport();
        var broker = CreateBroker();
        var task = broker.RequestAsync(transport, Origin, OriginId, Input, _ => true, null, publishToThread: true);
        string code = GetCode(await transport.Reply.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.True(broker.TryHandle(Origin with { Text = "answer broken" }, OriginId));
        Assert.True(broker.TryHandle(Origin with { Text = $"answer {code} 1 9" }, OriginId));
        Assert.True(broker.TryHandle(Origin with { Text = $"answer {new string('A', 12)} 1 1" }, OriginId));
        Assert.False(task.IsCompleted);

        Assert.True(broker.TryHandle(Origin with { Text = $"answer {code} 1 1,2" }, OriginId));
        Assert.True(broker.TryHandle(Origin with { Text = $"answer {code} 2 free response" }, OriginId));
        Assert.NotNull(await task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task AuthorizationIsCheckedBeforePublishingAndAtRequestCreation()
    {
        var transport = new FakeTransport();
        var broker = CreateBroker();

        var result = await broker.RequestAsync(transport, Origin, OriginId, Input, _ => false, null,
            publishToThread: true);

        Assert.Null(result);
        Assert.False(transport.Reply.Task.IsCompleted);
    }

    [Fact]
    public async Task PrivateRequestUsesOnlyLocalHandler()
    {
        var transport = new FakeTransport();
        var broker = CreateBroker();
        IReadOnlyDictionary<string, string[]> expected = new Dictionary<string, string[]> { ["private-text-id"] = ["local"] };

        var result = await broker.RequestAsync(transport, Origin, OriginId, Input, _ => true,
            (_, _) => Task.FromResult<IReadOnlyDictionary<string, string[]>?>(expected));

        Assert.Same(expected, result);
        Assert.False(transport.Reply.Task.IsCompleted);
    }

    [Fact]
    public async Task TimedOutCodeIsRemovedAndStaleCommandIsConsumed()
    {
        var transport = new FakeTransport();
        var broker = CreateBroker(TimeSpan.FromMilliseconds(75));
        var task = broker.RequestAsync(transport, Origin, OriginId, Input, _ => true, null,
            publishToThread: true);
        string code = GetCode(await transport.Reply.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Null(await task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(broker.TryHandle(Origin with { Text = $"answer {code} 1 1" }, OriginId));
    }

    [Fact]
    public async Task CallerCancellationRemovesPendingCodeAndAllowsANewOne()
    {
        var transport = new FakeTransport();
        var broker = CreateBroker(TimeSpan.FromSeconds(3));
        using var cancellation = new CancellationTokenSource();
        var cancelledRequest = broker.RequestAsync(transport, Origin, OriginId, Input, _ => true, null,
            cancellation.Token, publishToThread: true);
        string oldCode = GetCode(await transport.Reply.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRequest);
        Assert.True(broker.TryHandle(Origin with { Text = $"answer {oldCode} 1 1" }, OriginId));

        var nextTransport = new FakeTransport();
        var next = broker.RequestAsync(nextTransport, Origin, OriginId, Input, _ => true, null, publishToThread: true);
        string newCode = GetCode(await nextTransport.Reply.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.NotEqual(oldCode, newCode);
        broker.TryHandle(Origin with { Text = $"answer {newCode} 1 1" }, OriginId);
        broker.TryHandle(Origin with { Text = $"answer {newCode} 2 reply" }, OriginId);
        Assert.NotNull(await next.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task MaximumPendingLimitFallsBackToLocalInput()
    {
        var broker = new ChatInputBroker((_, code) => $"code {code}", TimeSpan.FromSeconds(3), maximumPending: 1);
        var firstTransport = new FakeTransport();
        var first = broker.RequestAsync(firstTransport, Origin, OriginId, Input, _ => true, null,
            publishToThread: true);
        string code = GetCode(await firstTransport.Reply.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        var secondTransport = new FakeTransport();
        IReadOnlyDictionary<string, string[]> localResult = new Dictionary<string, string[]> { ["private-text-id"] = ["local"] };

        var second = await broker.RequestAsync(secondTransport, Origin with { MessageId = "second" }, OriginId, Input,
            _ => true, (_, _) => Task.FromResult<IReadOnlyDictionary<string, string[]>?>(localResult),
            publishToThread: true);

        Assert.Same(localResult, second);
        Assert.False(secondTransport.Reply.Task.IsCompleted);
        broker.TryHandle(Origin with { Text = $"answer {code} 1 1" }, OriginId);
        broker.TryHandle(Origin with { Text = $"answer {code} 2 reply" }, OriginId);
        Assert.NotNull(await first.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task AuthorizationCallbackExceptionFailsClosed()
    {
        var transport = new FakeTransport();
        var broker = CreateBroker();

        var result = await broker.RequestAsync(transport, Origin, OriginId, Input,
            _ => throw new InvalidOperationException("private failure"), null, publishToThread: true);

        Assert.Null(result);
        Assert.False(transport.Reply.Task.IsCompleted);
    }

    [Fact]
    public async Task LongSupportedQuestionFallsBackToLocalWithoutTruncatingContent()
    {
        var question = Input.Questions[0] with { Question = new string('x', 4096), Header = new string('h', 256) };
        var input = Input with { Questions = [question] };
        var transport = new FakeTransport();
        var broker = new ChatInputBroker((q, _) => q.Questions[0].Question);
        AppServerInput? localInput = null;
        IReadOnlyDictionary<string, string[]> expected = new Dictionary<string, string[]> { [question.Id] = ["First"] };
        var result = await broker.RequestAsync(transport, Origin, OriginId, input, _ => true,
            (q, _) => { localInput = q; return Task.FromResult<IReadOnlyDictionary<string, string[]>?>(expected); },
            publishToThread: true);
        Assert.Same(expected, result);
        Assert.Equal(question.Id, localInput!.Questions[0].Id);
        Assert.Equal(question.Question, localInput.Questions[0].Question);
        Assert.Equal(question.Header, localInput.Questions[0].Header);
        Assert.Equal(question.Options.ToArray(), localInput.Questions[0].Options.ToArray());
        Assert.False(transport.Reply.Task.IsCompleted);
    }

    private static ChatInputBroker CreateBroker(TimeSpan? timeout = null) =>
        new((input, code) => $"Questions code {code}: {input.Questions.Count}", timeout);

    private static string GetCode(string prompt)
    {
        var match = System.Text.RegularExpressions.Regex.Match(prompt, @"(?i)code\s+([0-9a-f]{12})");
        Assert.True(match.Success, prompt);
        return match.Groups[1].Value;
    }

    private sealed class FakeTransport : IChatTransport
    {
        public TaskCompletionSource<string> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ReplyText => Reply.Task.IsCompletedSuccessfully ? Reply.Task.Result : string.Empty;
        public Task RunAsync(BridgeSettings settings, Func<ChatRequest, CancellationToken, Task> onRequest, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReplyAsync(ChatRequest request, string text, CancellationToken cancellationToken)
        {
            Reply.TrySetResult(text);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
