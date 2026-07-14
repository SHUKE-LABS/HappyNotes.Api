using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HappyNotes.Common;
using HappyNotes.Services.interfaces;
using Microsoft.Extensions.Logging;

namespace HappyNotes.Services;

/// <summary>
/// Generates embeddings via a self-hosted HTTP endpoint (default: Ollama). Every failure path returns
/// null rather than throwing, so a down/slow backend degrades search to keyword-only instead of erroring.
/// </summary>
public class EmbeddingService : IEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly SemanticSearchOptions _options;
    private readonly ILogger<EmbeddingService> _logger;

    public EmbeddingService(HttpClient httpClient, SemanticSearchOptions options, ILogger<EmbeddingService> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
        if (_options.EmbeddingTimeoutSeconds > 0)
        {
            _httpClient.Timeout = TimeSpan.FromSeconds(_options.EmbeddingTimeoutSeconds);
        }
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.EmbeddingEndpoint) || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var requestBody = new EmbeddingRequest { Model = _options.EmbeddingModel, Prompt = text };
            var content = new StringContent(JsonSerializer.Serialize(requestBody, JsonSerializerConfig.Default), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(_options.EmbeddingEndpoint, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Embedding backend returned {StatusCode}: {Error}", response.StatusCode, error);
                return null;
            }

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var parsed = JsonSerializer.Deserialize<EmbeddingResponse>(responseContent, JsonSerializerConfig.Default);
            var embedding = parsed?.Embedding;

            if (embedding == null || embedding.Length == 0)
            {
                _logger.LogWarning("Embedding backend returned an empty embedding");
                return null;
            }

            if (embedding.Length != _options.Dimensions)
            {
                _logger.LogWarning("Embedding dimension mismatch: expected {Expected}, got {Actual}. Check SemanticSearch:Dimensions vs the model output.",
                    _options.Dimensions, embedding.Length);
                return null;
            }

            return embedding;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate embedding; falling back to keyword-only behavior");
            return null;
        }
    }

    private sealed class EmbeddingRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
        [JsonPropertyName("prompt")] public string Prompt { get; set; } = string.Empty;
    }

    private sealed class EmbeddingResponse
    {
        [JsonPropertyName("embedding")] public float[]? Embedding { get; set; }
    }
}
