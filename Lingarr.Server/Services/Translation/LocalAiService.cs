using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lingarr.Contracts.Exceptions;
using Lingarr.Contracts.Models.Batch;
using Lingarr.Contracts.Translation;
using Lingarr.Core.Configuration;
using Lingarr.Server.Exceptions;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models.Integrations.Translation;
using Lingarr.Server.Services.Translation.Base;

namespace Lingarr.Server.Services.Translation;

public class LocalAiService : BaseLanguageService, ITranslationService, IBatchTranslationService, IProofreadService
{
    private readonly HttpClient _httpClient;
    private readonly IRequestTemplateService _requestTemplateService;
    private readonly string _serviceName;
    private readonly string _modelSettingKey;
    private readonly string _endpointSettingKey;
    private readonly string _apiKeySettingKey;
    private readonly string _chatRequestTemplateSettingKey;
    private readonly string _generateRequestTemplateSettingKey;
    private string? _model;
    private string? _endpoint;
    private string? _chatRequestTemplate;
    private string? _generateRequestTemplate;
    private bool _isChatEndpoint;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <inheritdoc />
    public override string? ModelName => _model;

    // retry settings
    private int _maxRetries;
    private TimeSpan _retryDelay;
    private int _retryDelayMultiplier;

    public LocalAiService(
        ISettingService settings,
        HttpClient httpClient,
        ILogger<LocalAiService> logger,
        LanguageCodeService languageCodeService,
        IRequestTemplateService requestTemplateService,
        string serviceName = "Custom AI",
        string modelSettingKey = SettingKeys.Translation.LocalAi.Model,
        string endpointSettingKey = SettingKeys.Translation.LocalAi.Endpoint,
        string apiKeySettingKey = SettingKeys.Translation.LocalAi.ApiKey,
        string chatRequestTemplateSettingKey = SettingKeys.Translation.LocalAi.ChatRequestTemplate,
        string generateRequestTemplateSettingKey = SettingKeys.Translation.LocalAi.GenerateRequestTemplate)
        : base(settings, logger, languageCodeService)
    {
        _httpClient = httpClient;
        _requestTemplateService = requestTemplateService;
        _serviceName = serviceName;
        _modelSettingKey = modelSettingKey;
        _endpointSettingKey = endpointSettingKey;
        _apiKeySettingKey = apiKeySettingKey;
        _chatRequestTemplateSettingKey = chatRequestTemplateSettingKey;
        _generateRequestTemplateSettingKey = generateRequestTemplateSettingKey;
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

        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;

            var settings = await _settings.GetSettings([
                _modelSettingKey,
                _endpointSettingKey,
                _chatRequestTemplateSettingKey,
                _generateRequestTemplateSettingKey,
                SettingKeys.Translation.AiPrompt,
                SettingKeys.Translation.AiUserPrompt,
                SettingKeys.Translation.ProofreadPrompt,
                SettingKeys.Translation.ProofreadUserPrompt,
                SettingKeys.Translation.RequestTimeout,
                SettingKeys.Translation.MaxRetries,
                SettingKeys.Translation.RetryDelay,
                SettingKeys.Translation.RetryDelayMultiplier,
                SettingKeys.Translation.LanguageCodeFormat
            ]);
            _model = settings[_modelSettingKey];
            _endpoint = settings[_endpointSettingKey];
            _chatRequestTemplate = !string.IsNullOrEmpty(settings[_chatRequestTemplateSettingKey])
                ? settings[_chatRequestTemplateSettingKey]
                : _requestTemplateService.GetDefaultTemplate(_chatRequestTemplateSettingKey);
            _generateRequestTemplate = !string.IsNullOrEmpty(settings[_generateRequestTemplateSettingKey])
                ? settings[_generateRequestTemplateSettingKey]
                : _requestTemplateService.GetDefaultTemplate(_generateRequestTemplateSettingKey);

            if (string.IsNullOrEmpty(_model) || string.IsNullOrEmpty(_endpoint))
            {
                throw new InvalidOperationException($"{_serviceName} requires both endpoint address and model name to be configured in settings.");
            }

            SetLanguageReplacements(sourceLanguage, targetLanguage, settings[SettingKeys.Translation.LanguageCodeFormat]);
            _prompt = settings[SettingKeys.Translation.AiPrompt];
            _userPrompt = settings[SettingKeys.Translation.AiUserPrompt];
            _proofreadPrompt = settings.GetValueOrDefault(SettingKeys.Translation.ProofreadPrompt);
            _proofreadUserPrompt = settings.GetValueOrDefault(SettingKeys.Translation.ProofreadUserPrompt);
            _isChatEndpoint = _endpoint.TrimEnd('/').EndsWith("completions", StringComparison.OrdinalIgnoreCase);

            var requestTimeout = int.TryParse(settings[SettingKeys.Translation.RequestTimeout],
                out var timeOut)
                ? timeOut
                : 5;
            _httpClient.Timeout = TimeSpan.FromMinutes(requestTimeout);
            _httpClient.DefaultRequestHeaders.Accept.Clear();
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

            var apiKey = await _settings.GetEncryptedSetting(_apiKeySettingKey);
            if (!string.IsNullOrEmpty(apiKey))
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

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

        var replacements = GetReplacements(_model!, text, contextLinesBefore, contextLinesAfter);
        using var retry = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, retry.Token);

        var delay = _retryDelay;
        for (var attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                return await CompleteWithLocalAiApi(replacements, linked.Token);
            }
            catch (HttpRequestException ex) when (IsRetryable(ex.StatusCode))
            {
                if (attempt == _maxRetries)
                {
                    throw new TranslationException($"Retry limit reached after {ex.StatusCode}.", ex);
                }

                await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * _retryDelayMultiplier);
            }
            catch (TranslationResponseException ex)
            {
                if (attempt == _maxRetries)
                {
                    _logger.LogError(ex, "Too many requests. Max retries exhausted for text: {Text}", text);
                    throw new TranslationException("Too many requests. Retry limit reached.", ex);
                }

                await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * _retryDelayMultiplier);

                _logger.LogWarning(
                    "429 Too Many Requests. Retrying in {Delay}... (Attempt {Attempt}/{MaxRetries})",
                    delay, attempt, _maxRetries);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during translation attempt {Attempt}", attempt);
                throw new TranslationException("Unexpected error occurred during translation.", ex);
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

        var replacements = GetProofreadReplacements(_model!, sourceText, translatedText);
        using var retry = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, retry.Token);

        var delay = _retryDelay;
        for (var attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                return await CompleteWithLocalAiApi(replacements, linked.Token);
            }
            catch (HttpRequestException ex) when (IsRetryable(ex.StatusCode))
            {
                if (attempt == _maxRetries)
                {
                    throw new TranslationException($"Retry limit reached after {ex.StatusCode}.", ex);
                }

                await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * _retryDelayMultiplier);
            }
            catch (TranslationResponseException ex)
            {
                if (attempt == _maxRetries)
                {
                    _logger.LogError(ex, "Too many requests. Max retries exhausted for text: {Text}", translatedText);
                    throw new TranslationException("Too many requests. Retry limit reached.", ex);
                }

                await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * _retryDelayMultiplier);

                _logger.LogWarning(
                    "429 Too Many Requests. Retrying in {Delay}... (Attempt {Attempt}/{MaxRetries})",
                    delay, attempt, _maxRetries);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during proofread attempt {Attempt}", attempt);
                throw new TranslationException("Unexpected error occurred during proofread.", ex);
            }
        }

        throw new TranslationException("Proofread failed after maximum retry attempts.");
    }

    private async Task<string> CompleteWithLocalAiApi(
        Dictionary<string, string> replacements,
        CancellationToken cancellationToken)
    {
        return _isChatEndpoint
            ? await TranslateWithChatApi(replacements, cancellationToken)
            : await TranslateWithGenerateApi(replacements, cancellationToken);
    }

    /// <summary>
    /// Translates a batch of subtitles in a single API call using structured outputs fallback
    /// Since custom endpoints may not support structured outputs, we'll attempt structured format first,
    /// then fall back to regular parsing if needed. Responses that cannot be parsed are retried
    /// using the configured retry settings, as local models occasionally emit malformed JSON.
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
                return await TranslateBatchWithLocalAiApi(subtitleBatch, linked.Token);
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
            catch (TranslationParseException ex)
            {
                if (attempt == _maxRetries)
                {
                    _logger.LogError(ex, "Max retries exhausted for batch translation, the model kept returning an unparsable response");
                    throw new TranslationException("Retry limit reached after unparsable response.", ex);
                }

                _logger.LogWarning(
                    "{ServiceName} returned an unparsable response. Retrying in {Delay}... (Attempt {Attempt}/{MaxRetries})",
                    _serviceName, delay, attempt, _maxRetries);

                await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * _retryDelayMultiplier);
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

    private async Task<Dictionary<int, string>> TranslateBatchWithLocalAiApi(
        List<BatchSubtitleItem> subtitleBatch,
        CancellationToken cancellationToken)
    {
        if (!_isChatEndpoint)
        {
            return await TranslateBatchWithGenerateApi(subtitleBatch, cancellationToken);
        }

        // Try structured output first (OpenAI-compatible format)
        try
        {
            return await TranslateBatchWithStructuredOutput(subtitleBatch, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Structured output failed, falling back to JSON parsing");
            return await TranslateBatchWithJsonParsing(subtitleBatch, cancellationToken);
        }
    }

    private async Task<Dictionary<int, string>> TranslateBatchWithStructuredOutput(
        List<BatchSubtitleItem> subtitleBatch,
        CancellationToken cancellationToken)
    {
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
                                        type = "integer",
                                        description = "Position number of the subtitle item"
                                    },
                                    line = new
                                    {
                                        type = "string",
                                        description = "Translated subtitle text"
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
        var bodyJson = _requestTemplateService.BuildRequestBody(_chatRequestTemplate!, replacements);
        bodyJson = _requestTemplateService.SetRequestFields(bodyJson, new Dictionary<string, object?>
        {
            ["response_format"] = responseFormat
        });

        var requestContent = new StringContent(
            bodyJson,
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync(_endpoint, requestContent, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            if (IsRetryable(response.StatusCode))
            {
                throw new HttpRequestException(
                    $"{_serviceName} returned {response.StatusCode}", null, response.StatusCode);
            }
            _logger.LogError(
                "{ServiceName} structured output batch request failed with status {StatusCode}: {ResponseContent}",
                _serviceName, response.StatusCode,
                responseContent);
            throw new TranslationException(
                $"{_serviceName} structured output batch request failed with status {response.StatusCode}: {responseContent}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var chatResponse = JsonSerializer.Deserialize<ChatResponse>(responseBody);
        if (chatResponse?.Choices == null || chatResponse.Choices.Count == 0)
        {
            throw new TranslationException($"No completion choices returned from {_serviceName}");
        }

        var translatedJson = chatResponse.Choices[0].Message.Content;

        try
        {
            // Parse the wrapper object first, extract the translations array
            var responseWrapper = JsonSerializer.Deserialize<JsonElement>(translatedJson);
            if (!responseWrapper.TryGetProperty("translations", out var translationsElement))
            {
                throw new TranslationParseException("Response does not contain 'translations' property");
            }

            var translatedItems =
                JsonSerializer.Deserialize<List<StructuredBatchResponse>>(translationsElement.GetRawText());

            if (translatedItems == null)
            {
                throw new TranslationParseException("Failed to deserialize translated subtitles");
            }

            return MergeByPosition(translatedItems);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse structured JSON response: {Json}", translatedJson);
            throw new TranslationParseException("Failed to parse structured translated subtitles", ex);
        }
    }

    private async Task<Dictionary<int, string>> TranslateBatchWithJsonParsing(
        List<BatchSubtitleItem> subtitleBatch,
        CancellationToken cancellationToken)
    {
        var replacements = GetBatchReplacements(_model!, JsonSerializer.Serialize(subtitleBatch));
        var bodyJson = _requestTemplateService.BuildRequestBody(_chatRequestTemplate!, replacements);

        var requestContent = new StringContent(
            bodyJson,
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync(_endpoint, requestContent, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            if (IsRetryable(response.StatusCode))
            {
                throw new HttpRequestException(
                    $"{_serviceName} returned {response.StatusCode}", null, response.StatusCode);
            }
            _logger.LogError(
                "{ServiceName} JSON parsing batch request failed with status {StatusCode}: {ResponseContent}",
                _serviceName, response.StatusCode,
                responseContent);
            throw new TranslationException(
                $"{_serviceName} JSON parsing batch request failed with status {response.StatusCode}: {responseContent}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var chatResponse = JsonSerializer.Deserialize<ChatResponse>(responseBody);

        if (chatResponse?.Choices == null || chatResponse.Choices.Count == 0)
        {
            throw new TranslationException($"No completion choices returned from {_serviceName}");
        }

        // Try to extract JSON
        var translatedJson = chatResponse.Choices[0].Message.Content;
        var jsonStart = translatedJson.IndexOf('[');
        var jsonEnd = translatedJson.LastIndexOf(']');
        if (jsonStart != -1 && jsonEnd != -1 && jsonEnd > jsonStart)
        {
            translatedJson = translatedJson.Substring(jsonStart, jsonEnd - jsonStart + 1);
        }

        try
        {
            var translatedItems = JsonSerializer.Deserialize<List<StructuredBatchResponse>>(translatedJson,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (translatedItems == null)
            {
                throw new TranslationParseException("Failed to deserialize translated subtitles from JSON parsing");
            }

            return MergeByPosition(translatedItems);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse JSON response: {Json}", translatedJson);
            throw new TranslationParseException("Failed to parse JSON translated subtitles", ex);
        }
    }

    private async Task<Dictionary<int, string>> TranslateBatchWithGenerateApi(
        List<BatchSubtitleItem> subtitleBatch,
        CancellationToken cancellationToken)
    {
        var replacements = GetBatchReplacements(_model!, JsonSerializer.Serialize(subtitleBatch));
        replacements["systemPrompt"] +=
            "\n\nPlease return the response as a JSON array with objects containing 'position' and 'line' fields. Example: [{\"position\": 1, \"line\": \"translated text\"}]";
        var bodyJson = _requestTemplateService.BuildRequestBody(_generateRequestTemplate!, replacements);
        bodyJson = _requestTemplateService.SetRequestFields(bodyJson, new Dictionary<string, object?>
        {
            ["stream"] = false
        });

        var content = new StringContent(bodyJson,
            Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(_endpoint, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            if (IsRetryable(response.StatusCode))
            {
                throw new HttpRequestException(
                    $"{_serviceName} returned {response.StatusCode}", null, response.StatusCode);
            }
            _logger.LogError(
                "{ServiceName} generate API batch request failed with status {StatusCode}: {ResponseContent}",
                _serviceName, response.StatusCode, responseContent);
            throw new TranslationException(
                $"{_serviceName} generate API batch request failed with status {response.StatusCode}: {responseContent}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var generateResponse = JsonSerializer.Deserialize<GenerateResponse>(responseBody);

        if (generateResponse == null || string.IsNullOrEmpty(generateResponse.Response))
        {
            throw new TranslationException("Invalid or empty response from generate API.");
        }

        var translatedJson = generateResponse.Response;

        // Try to extract JSON from the response
        var jsonStart = translatedJson.IndexOf('[');
        var jsonEnd = translatedJson.LastIndexOf(']');

        if (jsonStart != -1 && jsonEnd != -1 && jsonEnd > jsonStart)
        {
            translatedJson = translatedJson.Substring(jsonStart, jsonEnd - jsonStart + 1);
        }

        try
        {
            var translatedItems = JsonSerializer.Deserialize<List<StructuredBatchResponse>>(translatedJson);

            if (translatedItems == null)
            {
                throw new TranslationParseException("Failed to deserialize translated subtitles from generate API");
            }

            return MergeByPosition(translatedItems);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse generate API JSON response: {Json}", translatedJson);
            throw new TranslationParseException("Failed to parse generate API translated subtitles", ex);
        }
    }

    private async Task<string> TranslateWithGenerateApi(
        Dictionary<string, string> replacements,
        CancellationToken cancellationToken)
    {
        var bodyJson = _requestTemplateService.BuildRequestBody(_generateRequestTemplate!, replacements);


        var content = new StringContent(bodyJson,
            Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(_endpoint, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            if (IsRetryable(response.StatusCode))
            {
                throw new HttpRequestException(
                    $"{_serviceName} returned {response.StatusCode}", null, response.StatusCode);
            }
            _logger.LogError(
                "{ServiceName} generate API request failed with status {StatusCode}: {ResponseContent}",
                _serviceName, response.StatusCode, responseContent);
            throw new TranslationException(
                $"{_serviceName} generate API request failed with status {response.StatusCode}: {responseContent}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var generateResponse = JsonSerializer.Deserialize<GenerateResponse>(responseBody);

        if (generateResponse == null || string.IsNullOrEmpty(generateResponse.Response))
        {
            throw new TranslationException("Invalid or empty response from generate API.");
        }

        return generateResponse.Response;
    }

    private async Task<string> TranslateWithChatApi(
        Dictionary<string, string> replacements,
        CancellationToken cancellationToken)
    {
        var bodyJson = _requestTemplateService.BuildRequestBody(_chatRequestTemplate!, replacements);
        

        var content = new StringContent(bodyJson,
            Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(_endpoint, content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            if (IsRetryable(response.StatusCode))
            {
                throw new HttpRequestException(
                    $"{_serviceName} returned {response.StatusCode}", null, response.StatusCode);
            }
            _logger.LogError(
                "{ServiceName} chat API request to {Endpoint} failed with status {StatusCode}: {ResponseContent}",
                _serviceName, _endpoint,
                response.StatusCode, 
                responseContent);
            throw new TranslationException(
                $"{_serviceName} chat API request failed with status {response.StatusCode}: {responseContent}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var chatResponse = JsonSerializer.Deserialize<ChatResponse>(responseBody);

        if (chatResponse?.Choices == null || chatResponse.Choices.Count == 0)
        {
            throw new TranslationResponseException("Invalid or empty response from chat API.");
        }

        return chatResponse.Choices[0].Message.Content;
    }

    private static bool IsRetryable(HttpStatusCode? statusCode) =>
        statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
}
