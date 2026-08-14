using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Contracts.Exceptions;
using Lingarr.Contracts.Models.Batch;
using Lingarr.Core.Configuration;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Services;
using Lingarr.Server.Services.Translation;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace Lingarr.Server.Tests.Services.Translation;

public class OpenAiServiceTests
{
    [Fact]
    public async Task OpenRouterConfiguration_UsesOpenRouterSettingsAndEndpoints()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(request =>
                    request.Method == HttpMethod.Get &&
                    request.RequestUri == new Uri("https://openrouter.ai/api/v1/models") &&
                    request.Headers.Authorization!.Scheme == "Bearer" &&
                    request.Headers.Authorization.Parameter == "openrouter-key"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(JsonResponse("""{"data":[{"id":"anthropic/claude-sonnet-4"}]}"""));
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(request =>
                    request.Method == HttpMethod.Post &&
                    request.RequestUri == new Uri("https://openrouter.ai/api/v1/chat/completions") &&
                    request.Headers.Authorization!.Scheme == "Bearer" &&
                    request.Headers.Authorization.Parameter == "openrouter-key"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(JsonResponse("""{"choices":[{"message":{"content":"Hola"}}]}"""));

        var service = CreateOpenRouterService(handler.Object);

        var models = await service.GetModels();
        var translation = await service.TranslateAsync(
            "Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Equal("anthropic/claude-sonnet-4", Assert.Single(models.Options!).Value);
        Assert.Equal("Hola", translation);
    }

    [Fact]
    public async Task TranslateAsync_ShouldRejectPartialOpenRouterErrorResponse()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(JsonResponse(
                """{"choices":[{"finish_reason":"error","message":{"content":"Partial output"}}]}"""));
        var service = CreateOpenRouterService(handler.Object);

        var exception = await Assert.ThrowsAsync<TranslationException>(() => service.TranslateAsync(
            "Hello", "en", "es", null, null, CancellationToken.None));

        Assert.Contains("Failed to translate using OpenRouter", exception.Message);
    }

    [Fact]
    public async Task TranslateBatchAsync_ShouldRequireStructuredOutputSupport()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(request =>
                    request.Content!.ReadAsStringAsync().GetAwaiter().GetResult().Contains("\"strict\":true") &&
                    request.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("\"require_parameters\":true")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(JsonResponse(
                """{"choices":[{"finish_reason":"stop","message":{"content":"{\"translations\":[{\"position\":1,\"line\":\"Hola\"}]}"}}]}"""));
        var service = CreateOpenRouterService(handler.Object);

        var result = await service.TranslateBatchAsync(
            [new BatchSubtitleItem { Position = 1, Line = "Hello" }],
            "en", "es", CancellationToken.None);

        Assert.Equal("Hola", result[1]);
    }

    private static OpenAiService CreateOpenRouterService(HttpMessageHandler handler)
    {
        var settings = new Mock<ISettingService>();
        settings.Setup(service => service.GetSettings(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new Dictionary<string, string>
            {
                [SettingKeys.Translation.OpenRouter.Model] = "anthropic/claude-sonnet-4",
                [SettingKeys.Translation.OpenRouter.RequestTemplate] = "",
                [SettingKeys.Translation.AiPrompt] = "Translate from {sourceLanguage} to {targetLanguage}",
                [SettingKeys.Translation.AiUserPrompt] = "{lineToTranslate}",
                [SettingKeys.Translation.RequestTimeout] = "5",
                [SettingKeys.Translation.MaxRetries] = "3",
                [SettingKeys.Translation.RetryDelay] = "0",
                [SettingKeys.Translation.RetryDelayMultiplier] = "1",
                [SettingKeys.Translation.LanguageCodeFormat] = "false"
            });
        settings.Setup(service => service.GetEncryptedSetting(SettingKeys.Translation.OpenRouter.ApiKey))
            .ReturnsAsync("openrouter-key");

        return new OpenAiService(
            settings.Object,
            Mock.Of<ILogger<OpenAiService>>(),
            new LanguageCodeService(),
            new RequestTemplateService(),
            new HttpClient(handler),
            endpoint: "https://openrouter.ai/api/v1/",
            serviceName: "OpenRouter",
            modelSettingKey: SettingKeys.Translation.OpenRouter.Model,
            apiKeySettingKey: SettingKeys.Translation.OpenRouter.ApiKey,
            requestTemplateSettingKey: SettingKeys.Translation.OpenRouter.RequestTemplate,
            modelsHttpClient: new HttpClient(handler),
            requireSupportedParameters: true);
    }

    private static HttpResponseMessage JsonResponse(string body) => new()
    {
        StatusCode = HttpStatusCode.OK,
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
}
