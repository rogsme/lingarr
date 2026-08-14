using Lingarr.Contracts.Interfaces.Plugins;
using Lingarr.Contracts.Plugins;
using Lingarr.Core.Configuration;

namespace Lingarr.Server.Services.Plugins.Manifests;

public sealed class OpenRouterPluginManifest : IPluginManifest
{
    public string Provider => "openrouter";

    public string DisplayName => "OpenRouter";

    public string? Description =>
        "OpenRouter provides access to OpenAI-compatible chat completion models from multiple providers.";

    public bool HasRequestTemplate => true;

    public IReadOnlyList<PluginSettingField> Settings { get; } =
    [
        new()
        {
            Key = SettingKeys.Translation.OpenRouter.ApiKey,
            Label = "API key",
            Type = PluginSettingType.Secret,
            Required = true,
            Description = "OpenRouter API key. Stored encrypted.",
            MinLength = 1,
            ValidationErrorMessage = "Value must not be empty"
        },
        new()
        {
            Key = SettingKeys.Translation.OpenRouter.Model,
            Label = "AI Model",
            Type = PluginSettingType.RemoteDropdown,
            Required = true,
            OptionsEndpoint = "/api/plugin/openrouter/models",
            Description = "Select a model from the OpenRouter catalogue."
        }
    ];
}
