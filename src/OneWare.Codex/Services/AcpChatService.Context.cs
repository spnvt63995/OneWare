namespace OneWare.Codex.Services;

internal sealed partial class AcpChatService
{
    internal const string ContextTierDefault = "Default";
    internal const string ContextTierLong = "Long Context";
    private readonly Func<CancellationToken, Task<IReadOnlyDictionary<string, AcpContextLimits>>> _readContextCatalog;
    private IReadOnlyDictionary<string, AcpContextLimits>? _contextCatalog;
    private (string? Kind, string Details) _contextIdentity;
    private long? _contextWindow;
    private AcpContextSize[] _contextSizes = [];

    public IReadOnlyList<AcpContextSize> ContextSizes => _contextSizes;
    public bool ShowContextSize => _contextSizes.Length > 1;
    public bool CanEditContextSize => CanEditOptions && _sending == 0;
    public AcpContextSize? SelectedContextSize =>
        _contextSizes.FirstOrDefault(x => x.Tier == _selection.ContextTier) ?? _contextSizes.FirstOrDefault();
    public string ContextSizeLabel => SelectedContextSize?.Label ?? "Context";
    public Task SelectContextSizeAsync(AcpContextSize choice) => ChangeOptionsAsync("context_window", choice.Tier);

    private async Task EnsureContextCatalogAsync(CancellationToken token)
    {
        var identity = (_authKind, _accountDetails);
        if (_contextCatalog != null && _contextIdentity == identity) return;
        IReadOnlyDictionary<string, AcpContextLimits> catalog = new Dictionary<string, AcpContextLimits>();
        // External providers need their own advertised limits; the local ChatGPT catalog is not evidence for them.
        if (!IsExternalAccount && _authKind != "none")
        {
            try { catalog = await _readContextCatalog(token); }
            catch (Exception) when (!token.IsCancellationRequested) { }
        }
        token.ThrowIfCancellationRequested();
        if (identity != (_authKind, _accountDetails)) return;
        _contextIdentity = identity;
        _contextCatalog = catalog;
    }

    private AcpContextLimits? ContextLimits(string model) =>
        _contextIdentity == (_authKind, _accountDetails) && _contextCatalog != null &&
        _contextCatalog.TryGetValue(model, out var limits) ? limits : null;

    private AcpContextSize[] ContextChoices(string model)
    {
        if (ContextLimits(model) is not { } limits) return [];
        var standard = new AcpContextSize(AcpContextSize.FormatTokens(limits.DefaultTokens),
            ContextTierDefault, limits.DefaultTokens);
        return limits.MaximumTokens > limits.DefaultTokens
            ? [standard, new(AcpContextSize.FormatTokens(limits.MaximumTokens), ContextTierLong, limits.MaximumTokens)]
            : [standard];
    }

    private long? ContextWindow(string model, string tier) => ContextLimits(model) is { } limits
        ? tier == ContextTierLong ? limits.MaximumTokens : limits.DefaultTokens : null;

    private void RefreshContextProjection()
    {
        var choices = Choices("model").Any(x => x.Value == EffectiveModel) ? ContextChoices(EffectiveModel) : [];
        if (!_contextSizes.SequenceEqual(choices)) _contextSizes = choices;
        foreach (var name in new[] { nameof(ContextSizes), nameof(ShowContextSize), nameof(CanEditContextSize),
                     nameof(SelectedContextSize), nameof(ContextSizeLabel) }) OnPropertyChanged(name);
    }
}
