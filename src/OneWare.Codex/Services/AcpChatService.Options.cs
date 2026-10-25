using System.ComponentModel;
using System.Text.Json;
using Avalonia.Threading;
using OneWare.Essentials.Services;

namespace OneWare.Codex.Services;

internal sealed record AcpSelection(string Model, string? Effort, string ContextTier = AcpChatService.DefaultContextTier);
internal sealed record AcpOptionChoice(string Value, string Label, string? Description);

internal sealed partial class AcpChatService
{
    internal const string SelectionKey = "Codex_AcpSelectionV1";
    private ISettingsService? _settings;
    private IChatAgentService? _agents;
    private AcpSelection _selection = null!;
    private AcpSelection _defaults = null!;
    private AcpOptionChoice[] _models = [];
    private AcpOptionChoice[] _filteredModels = [];
    private AcpOptionChoice[] _efforts = [];
    private bool _updatingOptions;
    private readonly SemaphoreSlim _optionsGate = new(1);
    private int _projection;
    private string? _optionsError;
    private string _modelSearchText = "";

    public bool IsProjectingOptions { get; private set; }
    public IReadOnlyList<AcpOptionChoice> Models => _models;
    public IReadOnlyList<AcpOptionChoice> FilteredModels => _filteredModels;
    public IReadOnlyList<AcpOptionChoice> ReasoningEfforts => _efforts;
    public AcpOptionChoice? SelectedModelChoice => _models.FirstOrDefault(x => x.Value == EffectiveModel);
    public AcpOptionChoice? SelectedReasoningEffort => _efforts.FirstOrDefault(x => x.Value == EffectiveEffort);
    private string EffectiveModel => _agent()?.Model ?? _selection.Model;
    private string? EffectiveEffort => _agent()?.ReasoningEffort ?? _selection.Effort;
    public bool AgentOptionsLocked => _agent() is { } agent &&
                                      (agent.Model != null || agent.ReasoningEffort != null);
    public bool CanEditOptions => CanLoadOptions && !AgentOptionsLocked;
    public bool CanLoadOptions => !_disposed && !_family.AccountChanging && _authKind != "none" &&
                                  (_sending == 0 || _prompt is { Started: true } turn && Current(turn));
    public bool CanEditReasoning => CanEditOptions && _efforts.Length > 0;
    public bool ShowReasoningEffort => _efforts.Length > 0 || EffectiveEffort != null;
    public string ModelLabel => (SelectedModelChoice?.Label ?? EffectiveModel) +
                                (AgentOptionsLocked ? " (agent)" : "");
    public string ReasoningLabel => (SelectedReasoningEffort?.Label ?? EffectiveEffort ?? "Default") +
                                    (AgentOptionsLocked ? " (agent)" : "");
    public string OptionsStatus => _updatingOptions ? "Loading models…" : _optionsError ??
        (_prompt is { Input: { } input } && (input.Model != EffectiveModel || input.Effort != EffectiveEffort)
            ? "Applies to the next message." : "");
    public bool HasOptionsStatus => !string.IsNullOrEmpty(OptionsStatus);
    public string ModelDescription => AgentOptionsLocked
        ? "Model and reasoning are set by the selected agent."
        : SelectedModelChoice?.Description ?? "Choose a model";
    public string ReasoningDescription => AgentOptionsLocked
        ? "Model and reasoning are set by the selected agent."
        : "Choose the reasoning effort for the selected model";

    public string ModelSearchText
    {
        get => _modelSearchText;
        set
        {
            if (SetProperty(ref _modelSearchText, value)) RefreshOptionsProjection();
        }
    }

    private void InitializeOptions(ISettingsService? settings, IChatAgentService? agents)
    {
        _selection = _defaults = new(_profile.Model, _profile.Effort);
        _settings = settings;
        _agents = agents;
        if (agents != null) agents.PropertyChanged += AgentOptionsChanged;
        if (settings == null) return;
        if (!settings.HasSetting(SelectionKey)) settings.Register(SelectionKey, "");
        RestoreDefaults();
        settings.WhenLoaded(() =>
        {
            if (_disposed) return;
            RestoreDefaults();
            RefreshOptionsProjection();
        });
    }

    private void RestoreDefaults()
    {
        try
        {
            var value = _settings!.GetSettingValue<string>(SelectionKey);
            if (!string.IsNullOrWhiteSpace(value) &&
                JsonSerializer.Deserialize<AcpSelection>(value) is { } saved &&
                !string.IsNullOrWhiteSpace(saved.Model))
                _defaults = saved;
        }
        catch (JsonException)
        {
            // Leave an unreadable saved value intact until an ordinary user selection replaces it.
        }
        if (_session == null) _selection = _defaults;
    }

    private void AgentOptionsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(IChatAgentService.SelectedAgent)) PostOptions();
    }

    public Task RefreshOptionsAsync() => ChangeOptionsAsync(null, null);
    public Task SelectModelAsync(AcpOptionChoice choice) => ChangeOptionsAsync("model", choice.Value);
    public Task SelectReasoningAsync(AcpOptionChoice choice) => ChangeOptionsAsync("reasoning_effort", choice.Value);

    private async Task ChangeOptionsAsync(string? id, string? value, bool loadingCommands = false)
    {
        // The active response keeps its model. ACP accepts session config changes for
        // the next prompt; do not wait on the gate held for the whole running turn.
        if (id == null && _updatingOptions || !CanLoadOptions || id != null && !CanEditOptions) return;
        if (id == "context_window" && !CanEditContextSize) return;
        await _submission.WaitAsync();
        var entered = false;
        var configuring = false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try
        {
            if (!CanLoadOptions || id != null && !CanEditOptions) return;
            if (id == "context_window" && !CanEditContextSize) return;
            if (id == null && _sending != 0)
            {
                await PublishOptionsAsync();
                return;
            }
            long epoch;
            Prompt? active;
            lock (_sync)
            {
                epoch = _submissionEpoch;
                active = _prompt;
                if (active == null && _sending != 0) return;
            }
            _updatingOptions = true;
            _optionsError = null;
            await PublishOptionsAsync();
            if (active == null)
            {
                await _gate.WaitAsync(cancellation.Token);
                entered = true;
            }
            await _optionsGate.WaitAsync(cancellation.Token);
            configuring = true;
            lock (_sync)
                if (epoch != _submissionEpoch) throw new OperationCanceledException();
            var context = await _host.CaptureContextAsync();
            if (active == null)
                await EnsureSessionAsync(Path.GetFullPath(context.Directory), await ToolsAsync(), cancellation.Token);
            else if (!ReferenceEquals(_connection, active.Connection) || _session != active.Session ||
                     _generation != active.Generation || Path.GetFullPath(context.Directory) != active.Directory)
                throw new OperationCanceledException();
            var connection = _connection;
            var session = _session;
            var generation = _generation;
            cancellation.Token.ThrowIfCancellationRequested();
            lock (_sync)
                if (epoch != _submissionEpoch) throw new OperationCanceledException();

            // ACP advertises model-dependent effort choices in the set-config reply.
            // Opening a picker prepares its catalog without sending a prompt or saving defaults.
            var model = id == "model" ? value! : id != null ? _selection.Model : EffectiveModel;
            if (!Choices("model").Any(x => x.Value == model))
            {
                _optionsError = "The selected model is unavailable. Choose another model.";
                return;
            }

            await SelectAsync("model", model, cancellation.Token);
            var efforts = Choices("reasoning_effort");
            var effort = id == "reasoning_effort" ? value : id != null ? _selection.Effort : EffectiveEffort;
            if (id == "model" && !efforts.Any(x => x.Value == effort))
                effort = CurrentOption("reasoning_effort");
            if (effort != null && efforts.Any(x => x.Value == effort))
                await SelectAsync("reasoning_effort", effort, cancellation.Token);
            else if (effort != null)
            {
                _optionsError = "The selected reasoning effort is unavailable. Choose another effort.";
                return;
            }

            if (id != null)
            {
                var tier = id == "context_window" ? value! : _selection.ContextTier;
                if (id == "context_window" && !ContextChoices(model).Any(x => x.Tier == tier))
                    throw new InvalidOperationException("This model does not offer that context length.");
                if (active == null)
                {
                    // Context is a launch/session setting. Resume the same session before saving the choice.
                    await EnsureSessionAsync(Path.GetFullPath(context.Directory), await ToolsAsync(),
                        cancellation.Token, model, tier);
                    await SelectAsync("model", model, cancellation.Token);
                    if (effort != null) await SelectAsync("reasoning_effort", effort, cancellation.Token);
                    connection = _connection;
                    session = _session;
                    generation = _generation;
                }
                var selection = new AcpSelection(model, effort, tier);
                // Keep the user's choice separate from agent overrides and remote observations.
                lock (_sync)
                {
                    if (_disposed || epoch != _submissionEpoch || !ReferenceEquals(connection, _connection) ||
                        session != _session || generation != _generation) throw new OperationCanceledException();
                    if (session != null) _profile.SaveSelection(session, selection);
                    _settings?.SetSettingValue(SelectionKey, JsonSerializer.Serialize(selection));
                    _selection = _defaults = selection;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            _optionsError = error is SessionResumeException ? error.Message : loadingCommands
                ? "Could not load ACP commands. Check the runtime and account, then reopen this menu to retry."
                : "Could not update model options. Try again.";
            Failure(error);
        }
        finally
        {
            if (configuring) _optionsGate.Release();
            if (entered) _gate.Release();
            _updatingOptions = false;
            await PublishOptionsAsync();
            _submission.Release();
        }
    }

    private JsonElement Option(string id)
    {
        if (_config.ValueKind == JsonValueKind.Array)
            foreach (var option in _config.EnumerateArray())
                if (Text(option, "id") == id) return option;
        return default;
    }

    private string? CurrentOption(string id) => Text(Option(id), "currentValue");

    private AcpOptionChoice[] Choices(string id)
    {
        var option = Option(id);
        if (option.ValueKind != JsonValueKind.Object ||
            !option.TryGetProperty("options", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return [];
        return FlattenChoices(choices).Where(x => !string.IsNullOrWhiteSpace(Text(x, "value")))
            .Select(x => new AcpOptionChoice(Text(x, "value")!, Text(x, "name") ?? Text(x, "value")!,
                Text(x, "description"))).DistinctBy(x => x.Value).ToArray();
    }

    private Task<bool> PublishOptionsAsync() => OneWareHost.Ui(() =>
    {
        RefreshOptionsProjection();
        return Task.FromResult(true);
    });

    private void PostOptions() => Dispatcher.UIThread.Post(RefreshOptionsProjection);

    private void RefreshOptionsProjection()
    {
        RefreshFooter();
        RefreshCommandProjection();
        var projection = ++_projection;
        IsProjectingOptions = true;
        RefreshContextProjection();
        var models = Choices("model");
        var efforts = CurrentOption("model") == EffectiveModel ? Choices("reasoning_effort") : [];
        if (!_models.SequenceEqual(models)) _models = models;
        var filtered = _models.Where(x =>
            x.Label.Contains(ModelSearchText, StringComparison.OrdinalIgnoreCase) ||
            x.Value.Contains(ModelSearchText, StringComparison.OrdinalIgnoreCase)).ToArray();
        // Keep the list and its focused row alive when only the accepted value changes.
        if (!_filteredModels.SequenceEqual(filtered)) _filteredModels = filtered;
        if (!_efforts.SequenceEqual(efforts)) _efforts = efforts;
        foreach (var property in new[]
                 {
                     nameof(Models), nameof(FilteredModels), nameof(SelectedModelChoice),
                     nameof(ReasoningEfforts), nameof(SelectedReasoningEffort), nameof(ModelLabel),
                     nameof(ReasoningLabel), nameof(CanEditOptions), nameof(CanEditReasoning),
                     nameof(CanLoadOptions), nameof(ShowReasoningEffort), nameof(AgentOptionsLocked),
                     nameof(OptionsStatus), nameof(HasOptionsStatus), nameof(ModelDescription),
                     nameof(ReasoningDescription)
                 })
            OnPropertyChanged(property);
        Dispatcher.UIThread.Post(() =>
        {
            if (projection != _projection) return;
            OnPropertyChanged(nameof(SelectedModelChoice));
            OnPropertyChanged(nameof(SelectedReasoningEffort));
            OnPropertyChanged(nameof(SelectedContextSize));
            IsProjectingOptions = false;
        }, DispatcherPriority.Background);
    }
}
