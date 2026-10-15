namespace ChatAgentBridge.Core;

internal static class AtomicFile
{
    /// <summary>Atomic rename, with bounded Windows sharing/access contention retries.</summary>
    internal static async Task ReplaceAsync(string source, string destination, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception error) when (OperatingSystem.IsWindows() && attempt < 4 &&
                error is IOException or UnauthorizedAccessException &&
                (error.HResult & 0xffff) is 5 or 32 or 33)
            {
                await Task.Delay(20 << attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
