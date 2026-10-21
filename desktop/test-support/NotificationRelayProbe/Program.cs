using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using ChatAgentBridge_Desktop.Notifications;

// Owner nonces and tokens travel only through redirected process streams. The smoke
// harness emits scenario names and counts; no raw activation credentials go to its log.
if (args.FirstOrDefault() == "owner")
{
    var queue = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    var calls = new int[3];
    var registry = new PendingNotificationRegistry();
    string Add(TimeSpan lifetime) => registry.Add(PendingNotificationKind.ChatRequest, lifetime,
        () => { calls[0]++; return Task.FromResult(PendingNotificationActionResult.Completed); },
        () => { calls[1]++; return Task.FromResult(PendingNotificationActionResult.Completed); },
        () => { calls[2]++; return Task.CompletedTask; }).Token;
    var approve = Add(TimeSpan.FromMinutes(1));
    var decline = Add(TimeSpan.FromMinutes(1));
    var open = Add(TimeSpan.FromMinutes(1));
    var expired = Add(TimeSpan.Zero);
    using var owner = new NotificationActivationRelay();
    owner.Start((token, action) => queue.Writer.TryWrite(async () => { await registry.InvokeAsync(token, action); }));
    var drain = Task.Run(async () =>
    {
        await foreach (var operation in queue.Reader.ReadAllAsync()) await operation();
    });
    Console.WriteLine(JsonSerializer.Serialize(new Handshake(owner.OwnerId, approve, decline, open, expired)));
    while (await Console.In.ReadLineAsync() is { } command && command != "stop")
    {
        if (command != "report") continue;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Writer.TryWrite(() => { barrier.SetResult(); return Task.CompletedTask; });
        await barrier.Task;
        Console.WriteLine(JsonSerializer.Serialize(new Counts(calls[0], calls[1], calls[2])));
    }
    owner.Dispose();
    queue.Writer.TryComplete();
    await drain;
    return;
}

if (args.FirstOrDefault() == "client")
{
    var request = JsonSerializer.Deserialize<Request>((await Console.In.ReadLineAsync())! )!;
    var accepted = await NotificationActivationRelay.TryForwardAsync(request.OwnerId, request.Token, request.Action);
    Console.WriteLine(JsonSerializer.Serialize(accepted));
    return;
}

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
using var first = Start("owner");
using var second = Start("owner");
try
{
    var owner = await Read<Handshake>(first);
    var other = await Read<Handshake>(second);
    await Forward(owner.OwnerId, owner.Approve, "approve", expected: true);
    await Forward(owner.OwnerId, owner.Approve, "approve", expected: true);
    await Forward(owner.OwnerId, owner.Decline, "decline", expected: true);
    await Forward(owner.OwnerId, owner.Open, "open", expected: true);
    Check(await Report(first) == new Counts(1, 1, 1), "approve, decline, open, duplicate single use");

    await Forward(owner.OwnerId, owner.Expired, "approve", expected: true);
    Check(await Report(first) == new Counts(1, 1, 1), "expired decision does not execute");
    await Forward(other.OwnerId, owner.Open, "approve", expected: true);
    Check(await Report(second) == new Counts(0, 0, 0), "another owner cannot execute the decision");

    await first.StandardInput.WriteLineAsync("stop");
    await first.WaitForExitAsync(deadline.Token);
    Check(first.ExitCode == 0, "owner clean shutdown");
    await Forward(owner.OwnerId, owner.Open, "approve", expected: false);
    Console.WriteLine("PASS dead owner returns failure within sender deadline");
    await second.StandardInput.WriteLineAsync("stop");
    await second.WaitForExitAsync(deadline.Token);
    Check(second.ExitCode == 0, "second owner clean shutdown");
}
finally
{
    Stop(first);
    Stop(second);
}

Process Start(string mode)
{
    var start = new ProcessStartInfo("dotnet")
    {
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add(mode);
    return Process.Start(start) ?? throw new InvalidOperationException("Probe process could not start.");
}

async Task<T> Read<T>(Process process)
{
    var line = await process.StandardOutput.ReadLineAsync(deadline.Token);
    // Do not echo protocol data in exception text.
    if (line is null) throw new InvalidOperationException("Probe process ended before its response.");
    return JsonSerializer.Deserialize<T>(line) ?? throw new InvalidOperationException("Probe response was missing.");
}

async Task Forward(string ownerId, string token, string action, bool expected)
{
    using var client = Start("client");
    try
    {
        await client.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new Request(ownerId, token, action)));
        var accepted = await Read<bool>(client);
        await client.WaitForExitAsync(deadline.Token);
        if (accepted != expected || client.ExitCode != 0)
            throw new InvalidOperationException("Cross-process relay returned an unexpected result.");
    }
    finally { Stop(client); }
}

async Task<Counts> Report(Process process)
{
    await process.StandardInput.WriteLineAsync("report");
    return await Read<Counts>(process);
}

static void Check(bool passed, string scenario)
{
    if (!passed) throw new InvalidOperationException("FAIL " + scenario);
    Console.WriteLine("PASS " + scenario);
}

static void Stop(Process process)
{
    if (!process.HasExited) process.Kill(entireProcessTree: true);
}

internal sealed record Request(string OwnerId, string Token, string Action);
internal sealed record Handshake(string OwnerId, string Approve, string Decline, string Open, string Expired);
internal sealed record Counts(int Approve, int Decline, int Open);
