#if CAB_STARTUP_FIXTURE
using System.Text.Json;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Migrations;

namespace ChatAgentBridge_Desktop.Startup;

// Deliberately absent from ordinary portable and Store binaries.
internal static class StartupFailureFixture
{
    private static string Mode
    {
        get
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Length != 3 || args[1] != "--data-dir" || !Path.IsPathFullyQualified(args[2]))
                return "none";
            var path = Path.Combine(new SettingsStore().DirectoryPath, "startup-fixture.json");
            if (!File.Exists(path)) return "none";
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.GetProperty("mode").GetString() ?? "none";
        }
    }

    internal static TimeSpan Timeout => Mode is "page-error" or "page-hang" or "migration-hang" or "page-late"
        ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(30);

    internal static Task BeforePageAsync() => Mode switch
    {
        "page-error" => Task.FromException(new InvalidOperationException("Isolated startup page fixture.")),
        "page-hang" => Task.Delay(System.Threading.Timeout.Infinite),
        "page-late" => Task.Delay(TimeSpan.FromSeconds(2.5)),
        _ => Task.CompletedTask
    };

    internal static async Task BeforeMigrationAsync(IProgress<MigrationProgress> progress)
    {
        if (Mode == "migration-hang") await Task.Delay(System.Threading.Timeout.Infinite);
        if (Mode == "migration-slow")
        {
            progress.Report(new MigrationProgress("isolated-slow-migration", 1, 1, MigrationPhase.Running));
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }
}
#endif
