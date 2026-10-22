using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Updates;

if (args.Length < 1) throw new ArgumentException("Expected prepare or cleanup.");
switch (args[0])
{
    case "prepare" when args.Length == 3:
        await PrepareAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        break;
    case "cleanup" when args.Length == 4:
    {
        var install = Path.GetFullPath(args[1]);
        var data = Path.GetFullPath(args[2]);
        var work = Path.GetFullPath(args[3]);
        var root = Path.GetFullPath(PortableUpdateStageStore.GetWorkRoot(install, data));
        PortableUpdateStageStore.DeleteOwnedWorkDirectory(install, data, work);
        if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        break;
    }
    default: throw new ArgumentException("Usage: prepare <install> <data> | cleanup <install> <data> <work>");
}

static async Task PrepareAsync(string install, string data)
{
    var appAssembly = Path.Combine(install, "ChatAgentBridge.Desktop.dll");
    if (!Directory.Exists(install) || !File.Exists(Path.Combine(install, "ChatAgentBridge.Desktop.exe")) || !File.Exists(appAssembly)) throw new DirectoryNotFoundException("Published install directory is invalid.");
    Directory.CreateDirectory(data);
    var currentVersion = Path.Combine(install, "portable-release.json") is var releasePath && File.Exists(releasePath)
        ? JsonDocument.Parse(await File.ReadAllTextAsync(releasePath)).RootElement.GetProperty("releaseVersion").GetString()
        : System.Reflection.AssemblyName.GetAssemblyName(appAssembly).Version is { } assemblyVersion
            ? $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}"
            : null;
    if (string.IsNullOrWhiteSpace(currentVersion)) throw new InvalidDataException("Installed release version is missing.");
    if (!ReleaseVersion.TryParse(currentVersion, out var current)) throw new InvalidDataException("Installed release version is invalid.");
    var version = new ReleaseVersion(current.Major, current.Minor, checked(current.Patch + 1), false, 0).ToString();
    var tag = "v" + version;
    var asset = $"ChatAgentBridge.Portable-{version}-win-x64.zip";
    var candidate = new PortableRelease(version, tag, asset,
        new Uri($"https://github.com/mmiyaji/chat-agent-bridge/releases/download/{tag}/{asset}"),
        new string('0', 64), 0, new Uri($"https://github.com/mmiyaji/chat-agent-bridge/releases/tag/{tag}"));
    var work = Path.Combine(PortableUpdateStageStore.GetWorkRoot(install, data), Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(work);
    var zipPath = Path.Combine(work, PortableUpdateStageStore.ArchiveFileName);
    BuildArchive(zipPath, version);
    candidate = candidate with { Sha256 = await HashAsync(zipPath), Size = new FileInfo(zipPath).Length };
    var staged = await PortableArchiveStager.ExtractAsync(zipPath, Path.Combine(work, "stage"), candidate, default);
    await PortableUpdateStageStore.SaveAsync(work, install, data, "x64", candidate, staged);
    await new PortableUpdatePreferencesStore(data).SaveAsync(new PortableUpdatePreferences(1, false, true, null, candidate));
    await new SettingsStore(data).SaveAsync(new BridgeSettings
    {
        WorkspaceRoot = data, AgentExecutable = "unused", AgentArguments = "", AgentMode = "exec",
        AgentProvider = "custom", AutoConnectProvider = "none", TrustedWorkspaceRoots = data,
        AgentPermissionMode = "inherit", ChatApprovalMode = "always", ChatResultMode = "manual",
        FileAttachmentApprovalMode = "always"
    });
    await new OnboardingProgressStore(data).SaveAsync(new OnboardingProgress(4, "completed"));
    await new OnboardingConsentStore(data).SaveAsync(new OnboardingConsent(OnboardingConsentStore.CurrentPolicyVersion, DateTimeOffset.UtcNow));
    var store = new SettingsStore(data);
    await new ChatAgentBridge.Core.Localization.LanguagePreferencesStore(store).SaveAsync(new("en"));
    await new ChatAgentBridge.Core.Appearance.AppearancePreferencesStore(store).SaveAsync(new("dark"));
    Console.WriteLine(JsonSerializer.Serialize(new { install, data, work, version }, new JsonSerializerOptions { WriteIndented = true }));
}

static void BuildArchive(string path, string version)
{
    var payload = new Dictionary<string, byte[]>(StringComparer.Ordinal)
    {
        ["ChatAgentBridge.Desktop.exe"] = MakePe(), ["ChatAgentBridge.Desktop.dll"] = [1, 2, 3],
        ["ChatAgentBridge.Desktop.runtimeconfig.json"] = "{}"u8.ToArray(), ["ChatAgentBridge.Updater.exe"] = MakePe(),
        ["portable-release.json"] = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, releaseVersion = version, architecture = "x64" })
    };
    payload["portable-files.json"] = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, files = payload.Keys.Append("portable-files.json").Append("SHA256SUMS.txt").ToArray() });
    payload["SHA256SUMS.txt"] = System.Text.Encoding.UTF8.GetBytes(string.Join("\n", payload.Select(p => $"{Convert.ToHexStringLower(SHA256.HashData(p.Value))} *{p.Key}")) + "\n");
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var (name, bytes) in payload)
    {
        using var output = archive.CreateEntry("ChatAgentBridge.Portable/" + name).Open();
        output.Write(bytes);
    }
}

static byte[] MakePe()
{
    var bytes = new byte[128]; bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
    BitConverter.GetBytes(64).CopyTo(bytes, 0x3c); BitConverter.GetBytes(0x00004550u).CopyTo(bytes, 64); BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 68);
    return bytes;
}

static async Task<string> HashAsync(string path)
{
    await using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
}
