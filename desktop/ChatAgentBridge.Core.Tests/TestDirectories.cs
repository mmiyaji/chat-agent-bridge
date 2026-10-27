namespace ChatAgentBridge.Core.Tests;

internal static class TestDirectories
{
    /// <summary>
    /// Deletes a test's temp directory that held a copied executable. Windows can briefly retain the image after
    /// its process exits and refuse the delete with access denied, so retry; a leftover temp folder is not a test failure.
    /// </summary>
    public static void DeleteWithRetry(string path)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
