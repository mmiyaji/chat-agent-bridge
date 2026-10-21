using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core;

/// <summary>Discovers models without an AI turn and changes only an existing, scoped conversation.</summary>
public sealed class ConversationModelService
{
    private readonly SessionStore _sessions;
    private readonly Func<BridgeSettings, CancellationToken, Task<IReadOnlyList<ModelOption>>> _discover;
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private readonly Dictionary<(string Provider, string Executable, string Arguments, string Workspace), CatalogEntry> _catalogs = [];
    private readonly TimeProvider _clock;

    public ConversationModelService(SessionStore sessions,
        Func<BridgeSettings, CancellationToken, Task<IReadOnlyList<ModelOption>>>? discover = null,
        TimeProvider? clock = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _discover = discover ?? new ModelCatalogService().GetModelsAsync;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<ModelOption>> GetModelsAsync(BridgeSettings settings,
        CancellationToken token = default)
    {
        var provider = AgentProfile.ResolveProvider(settings);
        if (!ExecutionPolicy.IsWorkspaceTrusted(settings, settings.WorkspaceRoot))
            throw new InvalidOperationException("The conversation workspace is not trusted.");
        // This integration accepts manual model identifiers for these CLIs.
        // Do not spend an AI turn discovering a model catalog.
        if (AgentProfile.IsBasicCliProvider(provider)) return Array.Empty<ModelOption>();
        if (provider is not (AgentProfile.Codex or AgentProfile.Claude))
            throw new InvalidOperationException("This CLI does not support model discovery.");
        var key = (provider, settings.AgentExecutable, settings.AgentArguments, Path.GetFullPath(settings.WorkspaceRoot));
        await _catalogGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_catalogs.TryGetValue(key, out var cached) && cached.ExpiresAt > _clock.GetUtcNow())
                return cached.Models;
            var models = (await _discover(settings, token).ConfigureAwait(false)).ToArray();
            if (_catalogs.Count >= 16) _catalogs.Clear();
            _catalogs[key] = new(models, _clock.GetUtcNow().AddMinutes(5));
            return models;
        }
        finally { _catalogGate.Release(); }
    }

    public async Task ChangeAsync(SessionKey key, BridgeSettings settings, string? model,
        Func<CancellationToken, Task<bool>> canChange, CancellationToken token = default)
    {
        if (!await canChange(token).ConfigureAwait(false)) throw new UnauthorizedAccessException();
        var binding = await _sessions.FindExecutionBindingAsync(key, token).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Start a conversation before changing its model.");
        var provider = AgentProfile.ResolveProvider(settings);
        if (ConversationAgentService.ProviderOf(binding) != provider)
            throw new InvalidOperationException("The configured CLI does not match this conversation.");
        if (!ExecutionPolicy.IsWorkspaceTrusted(settings, binding.WorkspaceRoot))
            throw new InvalidOperationException("The conversation workspace is not trusted.");
        model = AgentProfile.NormalizeModel(model);
        if (model is not null && !AgentProfile.IsBasicCliProvider(provider))
        {
            var models = await GetModelsAsync(settings, token).ConfigureAwait(false);
            if (!models.Any(candidate => candidate.Id == model))
                throw new ArgumentException("Select a model returned by the configured CLI.", nameof(model));
        }
        if (!await canChange(token).ConfigureAwait(false)) throw new UnauthorizedAccessException();
        // Clear effort when changing models; a previous model's effort may be unsupported.
        await _sessions.UpdateModelAndEffortAsync(binding, model, null, token).ConfigureAwait(false);
    }

    private sealed record CatalogEntry(IReadOnlyList<ModelOption> Models, DateTimeOffset ExpiresAt);
}
