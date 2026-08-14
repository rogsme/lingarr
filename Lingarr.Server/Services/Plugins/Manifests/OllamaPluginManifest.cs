using Lingarr.Contracts.Interfaces.Plugins;
using Lingarr.Contracts.Plugins;
using Lingarr.Core.Configuration;

namespace Lingarr.Server.Services.Plugins.Manifests;

public sealed class OllamaPluginManifest : IPluginManifest
{
    public string Provider => "ollama";

    public string DisplayName => "Ollama";

    public string? Description =>
        "Self-hosted Ollama models using the native generate or OpenAI-compatible chat endpoint. The API key is optional.";

    public bool HasRequestTemplate => true;

    public IReadOnlyList<PluginSettingField> Settings { get; } =
    [
        new()
        {
            Key = SettingKeys.Translation.Ollama.Endpoint,
            Label = "Address",
            Type = PluginSettingType.Url,
            Required = true,
            Default = "http://ollama:11434/api/generate",
            Description = "Full URL to the generate or chat/completions endpoint. Ending in 'completions' selects the OpenAI-compatible path."
        },
        new()
        {
            Key = SettingKeys.Translation.Ollama.Model,
            Label = "AI Model",
            Type = PluginSettingType.Text,
            Required = true,
            Default = "aya-expanse",
            Description = "Model identifier exposed by Ollama."
        },
        new()
        {
            Key = SettingKeys.Translation.Ollama.ApiKey,
            Label = "API key (optional)",
            Type = PluginSettingType.Secret,
            Required = false,
            Description = "Bearer token if the deployment requires authentication. Stored encrypted."
        }
    ];
}
