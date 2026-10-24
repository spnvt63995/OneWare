namespace OneWare.Essentials.Services;

/// <summary>
/// Preferred for first use or when the saved provider is no longer installed.
/// The chat host still restores an existing selection of any installed provider.
/// </summary>
public interface IChatServiceWithDefaultPreference : IChatService;
