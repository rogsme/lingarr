using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lingarr.Contracts.Exceptions;
using Lingarr.Contracts.Models;
using Lingarr.Contracts.Models.Batch;
using Lingarr.Contracts.Translation;
using Lingarr.Core.Configuration;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models;
using Lingarr.Server.Services.Translation.Base;

namespace Lingarr.Server.Services.Translation;

public class OpenAiService : BaseLanguageService, ITranslationService, IBatchTranslationService, IProofreadService
{
    private readonly string _endpoint;
    private readonly string _serviceName;
    private readonly string _modelSettingKey;
    private readonly string _apiKeySettingKey;
    private readonly string _requestTemplateSettingKey;
    private readonly bool _requireSupportedParameters;
    private string? _model;
    private string? _apiKey;
    private string? _requestTemplate;
    private readonly HttpClient _httpClient;
    private readonly HttpClient _modelsHttpClient;
    private readonly IRequestTemplateService _requestTemplateService;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <inheritdoc />
    public override string? ModelName => _model;

    // retry settings
    private int _maxRetries;
    private TimeSpan _retryDelay;
    private int _retryDelayMultiplier;

    public OpenAiService(
        ISettingService settings,
        ILogger<OpenAiService> logger,
        LanguageCodeService languageCodeService,
        IRequestTemplateService requestTemplateService,
        HttpClient? httpClient = null,
        string endpoint = "https://api.openai.com/v1/",
        string serviceName = "OpenAI",
        string modelSettingKey = SettingKeys.Translation.OpenAi.Model,
        string apiKeySettingKey = SettingKeys.Translation.OpenAi.ApiKey,
        string requestTemplateSettingKey = SettingKeys.Translation.OpenAi.RequestTemplate,
        HttpClient? modelsHttpClient = null,
        bool requireSupportedParameters = false)
        : base(settings, logger, languageCodeService)
    {
        _httpClient = httpClient ?? new HttpClient();
        _modelsHttpClient = modelsHttpClient ?? new HttpClient();
        _requestTemplateService = requestTemplateService;
        _endpoint = endpoint.TrimEnd('/') + "/";
        _serviceName = serviceName;
        _modelSettingKey = modelSettingKey;
        _apiKeySettingKey = apiKeySettingKey;
        _requestTemplateSettingKey = requestTemplateSettingKey;
        _requireSupportedParameters = requireSupportedParameters;
    }

    /// <summary>
    /// Initializes the translation service with necessary configurations and credentials.
    /// This method is thread-safe and ensures one-time initialization of service dependencies.
    /// </summary>
    /// <param name="sourceLanguage">The source language code for translation</param>
    /// <param name="targetLanguage">The target language code for translation</param>
    /// <returns>A task that represents the asynchronous initialization operation</returns>
    /// <exception cref="InvalidOperationException">Thrown when required configuration settings are missing or invalid</exception>
    private async Task InitializeAsync(string sourceLanguage, string targetLanguage)
    {
        if (_initialized) return;

        try
        {
            await _initLock.WaitAsync();
            if (_initialized) return;

            var settings = await _settings.GetSettings([
                _modelSettingKey,
                _requestTemplateSettingKey,
                SettingKeys.Translation.AiPrompt,
                SettingKeys.Translation.AiUserPrompt,
                SettingKeys.Translation.ProofreadPrompt,
                SettingKeys.Translation.ProofreadUserPrompt,
                SettingKeys.Translation.ProofreadModel,
                SettingKeys.Translation.RequestTimeout,
                SettingKeys.Translation.MaxRetries,
                SettingKeys.Translation.RetryDelay,
                SettingKeys.Translation.RetryDelayMultiplier,
                SettingKeys.Translation.LanguageCodeFormat
            ]);

            _model = settings[_modelSettingKey];
            _apiKey = await _settings.GetEncryptedSetting(_apiKeySettingKey);
            _requestTemplate = !string.IsNullOrEmpty(settings[_requestTemplateSettingKey])
                ? settings[_requestTemplateSettingKey]
                : _requestTemplateService.GetDefaultTemplate(_requestTemplateSettingKey);

            if (string.IsNullOrEmpty(_model) || string.IsNullOrEmpty(_apiKey))
            {
                throw new InvalidOperationException($"{_serviceName} API key or model is not configured.");
            }

            SetLanguageReplacements(sourceLanguage, targetLanguage, settings[SettingKeys.Translation.LanguageCodeFormat]);
            _prompt = settings[SettingKeys.Translation.AiPrompt];
            _userPrompt = settings[SettingKeys.Translation.AiUserPrompt];
            _proofreadPrompt = settings.GetValueOrDefault(SettingKeys.Translation.ProofreadPrompt);
            _proofreadUserPrompt = settings.GetValueOrDefault(SettingKeys.Translation.ProofreadUserPrompt);
            _proofreadModel = settings.GetValueOrDefault(SettingKeys.Translation.ProofreadModel);

            var requestTimeout = int.TryParse(settings[SettingKeys.Translation.RequestTimeout],
                out var timeOut)
                ? timeOut
                : 5;
            _httpClient.Timeout = TimeSpan.FromMinutes(requestTimeout);
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

            _maxRetries = int.TryParse(settings[SettingKeys.Translation.MaxRetries], out var maxRetries) 
                ? maxRetries 
                : 5;
            var retryDelaySeconds = int.TryParse(settings[SettingKeys.Translation.RetryDelay], out var delaySeconds) 
                ? delaySeconds 
                : 1;
            _retryDelay = TimeSpan.FromSeconds(retryDelaySeconds);
            _retryDelayMultiplier = int.TryParse(settings[SettingKeys.Translation.RetryDelayMultiplier], out var multiplier) 
                ? multiplier 
                : 2;

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <inheritdoc />
    public override async Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        List<string>? contextLinesBefore,
        List<string>? contextLinesAfter,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(sourceLanguage, targetLanguage);

        using var retry = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, retry.Token);
        
        var delay = _retryDelay;
        for (var attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                var replacements = GetReplacements(_model!, text, contextLinesBefore, contextLinesAfter);
                return await CompleteWithOpenAiApi(replacements, linked.Token);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                if (attempt == _maxRetries)
                {
                    _logger.LogError(ex, "Max retries exhausted ({StatusCode}) for text: {Text}", ex.StatusCode, text);
                    throw new TranslationException($"Retry limit reached after {ex.StatusCode}.", ex);
                }

                await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * _retryDelayMultiplier);

                _logger.LogWarning(
                    "{ServiceName} received {StatusCode}. Retrying in {Delay}... (Attempt {Attempt}/{MaxRetries})",
                    _serviceName, ex.StatusCode, delay, attempt, _maxRetries);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during {ServiceName} translation", _serviceName);
                throw new TranslationException($"Failed to translate using {_serviceName}", ex);
            }
        }

        throw new TranslationException("Translation failed after maximum retry attempts.");
    }

    /// <inheritdoc />
    public async Task<string> ProofreadAsync(
        string sourceText,
        string translatedText,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(sourceLanguage, targetLanguage);

        using var retry = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, retry.Token);

        var delay = _retryDelay;
        for (var attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                var replacements = GetProofreadReplacements(_model!, sourceText, translatedText);
                return await CompleteWithOpenAiApi(replacements, linked.Token);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                if (attempt == _maxRetries)
                {
                    _logger.LogError(ex, "Max retries exhausted ({StatusCode}) for text: {Text}", ex.StatusCode, translatedText);
                    throw new TranslationException($"Retry limit reached after {ex.StatusCode}.", ex);
                }

                await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * _retryDelayMultiplier);

                _logger.LogWarning(
                    "{ServiceName} received {StatusCode}. Retrying in {Delay}... (Attempt {Attempt}/{MaxRetries})",
                    _serviceName, ex.StatusCode, delay, attempt, _maxRetries);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during {ServiceName} proofread", _serviceName);
                throw new TranslationException($"Failed to proofread using {_serviceName}", ex);
            }
        }

        throw new TranslationException("Proofread failed after maximum retry attempts.");
    }

    private async Task<string> CompleteWithOpenAiApi(
        Dictionary<string, string> replacements,
        CancellationToken cancellationToken)
    {
        var requestUrl = $"{_endpoint}chat/completions";
        var bodyJson = _requestTemplateService.BuildRequestBody(_requestTemplate!, replacements);
        var requestContent = new StringContent(
            bodyJson,
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync(requestUrl, requestContent, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                throw new HttpRequestException(
                    $"{_serviceName} returned {response.StatusCode}", null, response.StatusCode);
            }

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "{ServiceName} API request failed with status {StatusCode}: {ResponseContent}",
                _serviceName, response.StatusCode, responseContent);
            throw new TranslationException(
                $"{_serviceName} API request failed with status {response.StatusCode}: {responseContent}");
        }

        var completionResponse =
            await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(cancellationToken);
        if (completionResponse?.Choices == null || completionResponse.Choices.Count == 0)
        {
            throw new TranslationException($"No completion choices returned from {_serviceName}");
        }

        var choice = completionResponse.Choices[0];
        if (string.Equals(choice.FinishReason, "error", StringComparison.OrdinalIgnoreCase))
        {
            throw new TranslationException($"{_serviceName} returned a partial response after an upstream error");
        }

        return choice.Message.Content;
    }

    /// <summary>
    /// Translates a batch of subtitles in a single API call using structured outputs
    /// </summary>
    /// <param name="subtitleBatch">List of subtitles with position and content</param>
    /// <param name="sourceLanguage">Source language code</param>
    /// <param name="targetLanguage">Target language code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary mapping position to translated content</returns>
    public async Task<Dictionary<int, string>> TranslateBatchAsync(
        List<BatchSubtitleItem> subtitleBatch,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(sourceLanguage, targetLanguage);

        using var retry = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, retry.Token);
        
        var delay = _retryDelay;
        for (var attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                return await TranslateBatchWithOpenAiApi(subtitleBatch, linked.Token);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                if (attempt == _maxRetries)
                {
                    _logger.LogError(ex, "Max retries exhausted ({StatusCode}) for batch translation", ex.StatusCode);
                    throw new TranslationException($"Retry limit reached after {ex.StatusCode}.", ex);
                }

                await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * _retryDelayMultiplier);

                _logger.LogWarning(
                    "{ServiceName} received {StatusCode}. Retrying in {Delay}... (Attempt {Attempt}/{MaxRetries})",
                    _serviceName, ex.StatusCode, delay, attempt, _maxRetries);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during batch translation attempt {Attempt}", attempt);
                throw new TranslationException("Unexpected error occurred during batch translation.", ex);
            }
        }

        throw new TranslationException("Batch translation failed after maximum retry attempts.");
    }

    private async Task<Dictionary<int, string>> TranslateBatchWithOpenAiApi(
        List<BatchSubtitleItem> subtitleBatch,
        CancellationToken cancellationToken)
    {
        var requestUrl = $"{_endpoint}chat/completions";
        var responseFormat = new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "batch_translation_response",
                strict = true,
                schema = new
                {
                    type = "object",
                    properties = new
                    {
                        translations = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    position = new
                                    {
                                        type = "integer"
                                    },
                                    line = new
                                    {
                                        type = "string"
                                    }
                                },
                                required = new[] { "position", "line" },
                                additionalProperties = false
                            }
                        }
                    },
                    required = new[] { "translations" },
                    additionalProperties = false
                }
            }
        };

        var replacements = GetBatchReplacements(_model!, JsonSerializer.Serialize(subtitleBatch));
        var bodyJson = _requestTemplateService.BuildRequestBody(_requestTemplate!, replacements);
        var requestFields = new Dictionary<string, object?>
        {
            ["response_format"] = responseFormat
        };
        if (_requireSupportedParameters)
        {
            requestFields["provider"] = new { require_parameters = true };
        }
        bodyJson = _requestTemplateService.SetRequestFields(bodyJson, requestFields);

        var requestContent = new StringContent(
            bodyJson,
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync(requestUrl, requestContent, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                throw new HttpRequestException(
                    $"Batch translation using {_serviceName} API failed with {response.StatusCode}.",
                    null, response.StatusCode);
            }

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "{ServiceName} batch API request failed with status {StatusCode}: {ResponseContent}",
                _serviceName, response.StatusCode, responseContent);
            throw new TranslationException(
                $"{_serviceName} batch API request failed with status {response.StatusCode}: {responseContent}");
        }

        var completionResponse = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(cancellationToken);
        if (completionResponse?.Choices == null || completionResponse.Choices.Count == 0)
        {
            throw new TranslationException($"No completion choices returned from {_serviceName}");
        }
        
        var choice = completionResponse.Choices[0];
        if (string.Equals(choice.FinishReason, "error", StringComparison.OrdinalIgnoreCase))
        {
            throw new TranslationException($"{_serviceName} returned a partial batch response after an upstream error");
        }

        var translatedJson = choice.Message.Content;
        try
        {
            var responseWrapper = JsonSerializer.Deserialize<JsonElement>(translatedJson);
            if (!responseWrapper.TryGetProperty("translations", out var translationsElement))
            {
                throw new TranslationException("Response does not contain 'translations' property");
            }

            var translatedItems =
                JsonSerializer.Deserialize<List<StructuredBatchResponse>>(translationsElement.GetRawText());
            if (translatedItems == null)
            {
                throw new TranslationException("Failed to deserialize translated subtitles");
            }

            return MergeByPosition(translatedItems);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse translated JSON: {Json}", translatedJson);
            throw new TranslationException("Failed to parse translated subtitles", ex);
        }
    }

    /// <inheritdoc />
    public override async Task<ModelsResponse> GetModels()
    {
        var apiKey = await _settings.GetEncryptedSetting(
            _apiKeySettingKey
        );

        if (string.IsNullOrEmpty(apiKey))
        {
            return new ModelsResponse
            {
                Message = $"{_serviceName} API key is not configured."
            };
        }

        try
        {
            var requestUrl = $"{_endpoint}models";
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var response = await _modelsHttpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "Failed to fetch models. Status: {StatusCode}, response: {ResponseContent}",
                    response.StatusCode, responseContent);
                return new ModelsResponse
                {
                    Message = $"Failed to fetch models. Status: {response.StatusCode}, response: {responseContent}"
                };
            }

            var modelsResponse = await response.Content.ReadFromJsonAsync<ModelsListResponse>();

            if (modelsResponse?.Data == null)
            {
                return new ModelsResponse
                {
                    Message = $"No models data returned from {_serviceName} API."
                };
            }

            var labelValues = modelsResponse.Data
                .Select(model => new LabelValue
                {
                    Label = model.Id,
                    Value = model.Id
                })
                .ToList();

            return new ModelsResponse
            {
                Options = labelValues
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error fetching models from {ServiceName} API", _serviceName);
            return new ModelsResponse
            {
                Message = $"HTTP error fetching models from {_serviceName} API: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching models from {ServiceName} API", _serviceName);
            return new ModelsResponse
            {
                Message = $"Error fetching models from {_serviceName} API: {ex.Message}"
            };
        }
    }
}
