using Lingarr.Contracts.Interfaces.Plugins;
using Lingarr.Contracts.Plugins;
using Lingarr.Core.Configuration;

namespace Lingarr.Server.Services.Plugins.Manifests;

public sealed class LocalAiPluginManifest : IPluginManifest
{
    public string Provider => "localai";

    public string DisplayName => "Custom AI";

    public string? Description =>
        "Custom OpenAI-compatible deployments. The endpoint determines whether the chat/completions or generate protocol is used for compatibility with existing configurations; the API key is optional.";

    public bool HasRequestTemplate => true;

    public IReadOnlyList<PluginSettingField> Settings { get; } =
    [
        new()
        {
            Key = SettingKeys.Translation.LocalAi.Endpoint,
            Label = "Address",
            Type = PluginSettingType.Url,
            Required = true,
            Default = "http://localhost:8080/v1/chat/completions",
            Description = "Full URL to the chat/completions or generate endpoint. Ending in 'completions' selects the OpenAI-compatible path."
        },
        new()
        {
            Key = SettingKeys.Translation.LocalAi.Model,
            Label = "AI Model",
            Type = PluginSettingType.Text,
            Required = true,
            Default = "model-name",
            Description = "Model identifier the deployment exposes."
        },
        new()
        {
            Key = SettingKeys.Translation.LocalAi.ApiKey,
            Label = "API key (optional)",
            Type = PluginSettingType.Secret,
            Required = false,
            Description = "Bearer token if the deployment requires authentication. Stored encrypted."
        }
    ];
}
