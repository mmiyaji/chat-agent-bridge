using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace ChatAgentBridge_Desktop.Notifications;

/// <summary>
/// Relays one activation to its process-local owner. An acknowledgement means only that
/// the owner dispatcher accepted the work; token lifetime and single use belong to the owner registry.
/// </summary>
internal sealed class NotificationActivationRelay : IDisposable
{
    private const string PipePrefix = "ChatAgentBridge.Notification.";
    private const int FrameLength = 34;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private NamedPipeServerStream? _server;
    private Task? _listener;
    private bool _started;
    private bool _disposed;

    public string OwnerId { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <param name="enqueue">Must only enqueue onto the owner dispatcher and return immediately.</param>
    public void Start(Func<string, string, bool> enqueue)
    {
        ArgumentNullException.ThrowIfNull(enqueue);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) throw new InvalidOperationException("The notification relay is already started.");
            // Publish the first listening endpoint before returning to the caller.
            _server = CreateServer();
            _started = true;
            _listener = ListenAsync(_server, enqueue);
        }
    }

    public static async Task<bool> TryForwardAsync(string ownerId, string token, string action,
        CancellationToken cancellationToken = default)
    {
        if (!IsIdentifier(ownerId) || !IsIdentifier(token) || EncodeAction(action) == 0) return false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(2500));
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipePrefix + ownerId, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            var frame = new byte[FrameLength];
            frame[0] = 1;
            Encoding.ASCII.GetBytes(token, frame.AsSpan(1, 32));
            frame[33] = EncodeAction(action);
            // Send once. A lost acknowledgement must never replay a decision.
            await pipe.WriteAsync(frame, deadline.Token).ConfigureAwait(false);
            var acknowledgement = new byte[1];
            await pipe.ReadExactlyAsync(acknowledgement, deadline.Token).ConfigureAwait(false);
            return acknowledgement[0] == 1;
        }
        catch (Exception ex) when (IsPipeFailure(ex)) { return false; }
    }

    private NamedPipeServerStream CreateServer() => new(PipePrefix + OwnerId,
        PipeDirection.InOut, 1,
        OperatingSystem.IsWindows() ? PipeTransmissionMode.Message : PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, FrameLength, 1);

    private async Task ListenAsync(NamedPipeServerStream server, Func<string, string, bool> enqueue)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(2));
                    var frame = new byte[FrameLength];
                    await server.ReadExactlyAsync(frame, deadline.Token).ConfigureAwait(false);
                    var token = Encoding.ASCII.GetString(frame, 1, 32);
                    var action = DecodeAction(frame[33]);
                    var validBoundary = !OperatingSystem.IsWindows() || server.IsMessageComplete;
                    var queued = false;
                    if (frame[0] == 1 && validBoundary && IsIdentifier(token) && action is not null)
                    {
                        // This callback schedules work only; never await its eventual execution here.
                        try { queued = enqueue(token, action); }
                        catch { /* A dispatcher that is shutting down may reject the activation. */ }
                    }
                    await server.WriteAsync(new byte[] { queued ? (byte)1 : (byte)0 }, deadline.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (IsPipeFailure(ex)) { }
                finally { server.Dispose(); }

                lock (_gate)
                {
                    if (_disposed) return;
                    _server = server = CreateServer();
                }
            }
        }
        catch
        {
            // This background listener owns all its failures. Never fault an unobserved task
            // or log activation tokens, callback data, or the owner nonce.
        }
        finally
        {
            server.Dispose();
            lock (_gate) { _server = null; }
        }
    }

    private static bool IsIdentifier(string? value) => value is { Length: 32 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static byte EncodeAction(string? action) => action switch
    {
        "approve" => 1, "decline" => 2, "open" => 3, _ => 0
    };

    private static string? DecodeAction(byte action) => action switch
    {
        1 => "approve", 2 => "decline", 3 => "open", _ => null
    };

    private static bool IsPipeFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or OperationCanceledException or
            TimeoutException or ObjectDisposedException or InvalidOperationException;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            _server?.Dispose();
            // Dispose the source only after in-flight cancellation registrations have unwound.
            _ = (_listener ?? Task.CompletedTask).ContinueWith(_ => _stop.Dispose(),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
