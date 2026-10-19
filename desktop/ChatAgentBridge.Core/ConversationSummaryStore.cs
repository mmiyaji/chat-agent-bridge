using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core;

/// <summary>Protects conversation text before it is persisted.</summary>
public interface IConversationTextProtector
{
    string Protect(string text);
    string Unprotect(string protectedText);
}

/// <summary>Encrypts conversation text with Windows DPAPI for the current user.</summary>
public sealed class DpapiConversationTextProtector : IConversationTextProtector
{
    public string Protect(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Conversation text protection requires Windows DPAPI.");
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public string Unprotect(string protectedText)
    {
        ArgumentNullException.ThrowIfNull(protectedText);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Conversation text protection requires Windows DPAPI.");
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedText), null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}

/// <summary>A local, extractive brief of recent prompt and final-answer excerpts.</summary>
public sealed record ConversationSummary(string Text, string SourceProvider, string SessionId, DateTimeOffset UpdatedAt);

/// <summary>
/// Stores brief local excerpts from successful prompt/answer pairs. It does not call an AI model
/// or claim to reproduce the agent's native conversation history.
/// </summary>
public sealed class ConversationSummaryStore
{
    private const int CurrentSchemaVersion = 1;
    private const int MaxScopes = 200;
    private const int MaxPairs = 3;
    private const int MaxPromptChars = 600;
    private const int MaxAnswerChars = 1800;
    private const int MaxInputChars = 100_000;
    private const int MaxSummaryChars = 8000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate;
    private readonly IConversationTextProtector _protector;

    public ConversationSummaryStore(string? directory = null, IConversationTextProtector? protector = null)
    {
        DirectoryPath = Path.GetFullPath(directory ?? new SettingsStore().DirectoryPath);
        _filePath = Path.Combine(DirectoryPath, "conversation-summaries.json");
        _gate = PathGates.GetOrAdd(_filePath, static _ => new SemaphoreSlim(1, 1));
        _protector = protector ?? new DpapiConversationTextProtector();
    }

    public string DirectoryPath { get; }

    /// <summary>Records a successful turn as local, extractive excerpts, retaining at most three turns per session.</summary>
    public async Task RecordAsync(SessionKey key, string provider, string sessionId, string prompt, string finalAnswer,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        ValidateProvider(provider);
        ValidateSessionId(sessionId);
        ValidateText(prompt, nameof(prompt));
        ValidateText(finalAnswer, nameof(finalAnswer));
        cancellationToken.ThrowIfCancellationRequested();

        var pair = new NotePair(Excerpt(prompt, MaxPromptChars), Excerpt(finalAnswer, MaxAnswerChars));
        var now = DateTimeOffset.UtcNow;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var entries = document.Entries!;
            var entry = entries.FirstOrDefault(item => item.Key == key);
            var pairs = new List<NotePair>();
            // A different agent session starts a clean note history for this chat scope.
            if (entry is not null && entry.Provider == provider && entry.SessionId == sessionId)
                pairs.AddRange(DecodePairs(entry));
            pairs.Add(pair);
            if (pairs.Count > MaxPairs) pairs.RemoveRange(0, pairs.Count - MaxPairs);

            var noteText = Render(pairs);
            if (noteText.Length > MaxSummaryChars)
                throw new InvalidDataException("Conversation summary exceeded its storage limit.");
            var protectedContent = _protector.Protect(JsonSerializer.Serialize(pairs, JsonOptions));
            cancellationToken.ThrowIfCancellationRequested();

            var replacement = new StoredEntry(key, provider, sessionId, now, protectedContent);
            var index = entries.FindIndex(item => item.Key == key);
            if (index >= 0) entries[index] = replacement;
            else entries.Add(replacement);
            if (entries.Count > MaxScopes)
            {
                document.Entries = entries.OrderByDescending(item => item.UpdatedAt)
                    .Take(MaxScopes).ToList();
            }
            await WriteAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<ConversationSummary?> FindAsync(SessionKey key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = (await ReadAsync(cancellationToken).ConfigureAwait(false)).Entries!
                .FirstOrDefault(item => item.Key == key);
            if (entry is null) return null;
            var text = Render(DecodePairs(entry));
            if (text.Length > MaxSummaryChars)
                throw new InvalidDataException("Conversation summary exceeded its storage limit.");
            return new ConversationSummary(text, entry.Provider, entry.SessionId, entry.UpdatedAt);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync(SessionKey key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (document.Entries!.RemoveAll(item => item.Key == key) > 0)
                await WriteAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private List<NotePair> DecodePairs(StoredEntry entry)
    {
        try
        {
            var pairs = JsonSerializer.Deserialize<List<NotePair>>(_protector.Unprotect(entry.ProtectedContent), JsonOptions);
            if (pairs is null || pairs.Count is < 1 or > MaxPairs ||
                pairs.Any(pair => pair is null || pair.Prompt is null || pair.Answer is null ||
                    pair.Prompt.Length > MaxPromptChars || pair.Answer.Length > MaxAnswerChars))
                throw new InvalidDataException("Conversation summary content is invalid.");
            var copy = pairs.ToList();
            if (Render(copy).Length > MaxSummaryChars)
                throw new InvalidDataException("Conversation summary exceeded its storage limit.");
            return copy;
        }
        catch (JsonException ex) { throw new InvalidDataException("Conversation summary content is invalid.", ex); }
        catch (FormatException ex) { throw new InvalidDataException("Conversation summary content is invalid.", ex); }
        catch (CryptographicException ex) { throw new InvalidDataException("Conversation summary content cannot be decrypted.", ex); }
    }

    private async Task<StoredDocument> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return new StoredDocument { SchemaVersion = CurrentSchemaVersion, Entries = [] };
        try
        {
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<StoredDocument>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (document?.Entries is null || document.SchemaVersion != CurrentSchemaVersion)
                throw new InvalidDataException("Conversation summary store is empty, invalid, or unsupported.");
            if (document.Entries.Count > MaxScopes) throw new InvalidDataException("Conversation summary store exceeds its scope limit.");
            var keys = new HashSet<SessionKey>();
            foreach (var entry in document.Entries)
            {
                if (entry is null) throw new InvalidDataException("Conversation summary store contains an empty entry.");
                ValidateKey(entry.Key);
                ValidateProvider(entry.Provider);
                ValidateSessionId(entry.SessionId);
                if (entry.UpdatedAt == default || string.IsNullOrWhiteSpace(entry.ProtectedContent))
                    throw new InvalidDataException("Conversation summary metadata is invalid.");
                if (!keys.Add(entry.Key)) throw new InvalidDataException("Conversation summary store contains duplicate scopes.");
            }
            return document;
        }
        catch (JsonException ex) { throw new InvalidDataException("Conversation summary store contains invalid JSON.", ex); }
        catch (ArgumentException ex) { throw new InvalidDataException("Conversation summary store contains invalid metadata.", ex); }
    }

    private async Task WriteAsync(StoredDocument document, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(DirectoryPath);
        var temp = Path.Combine(DirectoryPath, $"conversation-summaries.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                document.SchemaVersion = CurrentSchemaVersion;
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await AtomicFile.ReplaceAsync(temp, _filePath, cancellationToken).ConfigureAwait(false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private const string TruncationMarker = "… [truncated]";

    private static string Excerpt(string text, int maxChars)
    {
        var normalized = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length <= maxChars) return normalized;
        return normalized[..(maxChars - TruncationMarker.Length)].TrimEnd() + TruncationMarker;
    }

    private static string Render(IReadOnlyList<NotePair> pairs)
        => "Brief conversation notes (extracts; not native agent history)\n\n" +
            string.Join("\n\n", pairs.Select((pair, index) => $"Turn {index + 1}\nRequest: {pair.Prompt}\nResponse: {pair.Answer}"));

    private static void ValidateText(string? text, string name)
    {
        if (text is null) throw new ArgumentNullException(name);
        if (text.Length > MaxInputChars) throw new ArgumentException("Conversation text exceeds the safe input limit.", name);
        if (text.Any(char.IsControl) && text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new ArgumentException("Conversation text contains unsupported control characters.", name);
    }

    private static void ValidateProvider(string? provider)
    {
        if (provider is not ("codex" or "claude"))
            throw new ArgumentException("Conversation summary provider must be 'codex' or 'claude'.", nameof(provider));
    }

    private static void ValidateSessionId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 256 || sessionId.Any(char.IsControl) ||
            sessionId.IndexOfAny(['/', '\\', '\0', ':']) >= 0)
            throw new ArgumentException("Conversation summary session id is invalid.", nameof(sessionId));
    }

    private static void ValidateKey(SessionKey? key)
    {
        if (key is null) throw new ArgumentException("Session key is required.", nameof(key));
        ValidateIdentifier(key.Provider, "provider");
        ValidateIdentifier(key.ChannelId, "channel id");
        ValidateIdentifier(key.ThreadId, "thread id");
        if (!key.Provider.Equals("slack", StringComparison.OrdinalIgnoreCase) &&
            !key.Provider.Equals("mattermost", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Session provider must be 'slack' or 'mattermost'.");
        if (key.UserId is null || key.Origin is null)
            throw new ArgumentException("Session user id and origin are required.");
        if (key.UserId is not null)
        {
            ValidateScopedIdentity(key.UserId, "user id");
            ValidateScopedIdentity(key.Origin!, "origin");
            if (key.Provider.Equals("slack", StringComparison.OrdinalIgnoreCase) && key.Origin != "slack")
                throw new ArgumentException("Slack session origin must be 'slack'.");
            if (key.Provider.Equals("mattermost", StringComparison.OrdinalIgnoreCase) &&
                (key.Origin!.Length != 64 || key.Origin.Any(c => !Uri.IsHexDigit(c))))
                throw new ArgumentException("Mattermost session origin must be a SHA-256 hex digest.");
        }
    }

    private static void ValidateScopedIdentity(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException($"Session {name} is invalid.");
    }

    private static void ValidateIdentifier(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value is "." or ".." ||
            value.Any(char.IsControl) || value.IndexOfAny(['/', '\\', '\0', ':']) >= 0)
            throw new ArgumentException($"Session {name} is invalid.");
    }

    private sealed class StoredDocument
    {
        public int? SchemaVersion { get; set; }
        public List<StoredEntry>? Entries { get; set; }
    }

    private sealed record StoredEntry(SessionKey Key, string Provider, string SessionId, DateTimeOffset UpdatedAt,
        string ProtectedContent);
    private sealed record NotePair(string Prompt, string Answer);
}
