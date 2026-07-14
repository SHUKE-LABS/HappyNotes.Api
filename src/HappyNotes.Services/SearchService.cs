using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Api.Framework.Extensions;
using HappyNotes.Common;
using HappyNotes.Common.Enums;
using HappyNotes.Entities;
using HappyNotes.Models.Search;
using HappyNotes.Services.interfaces;
using SqlSugar;

namespace HappyNotes.Services;

public class SearchService : ISearchService
{
    private readonly IDatabaseClient _client;
    private readonly HttpClient _httpClient;

    public SearchService(IDatabaseClient client, HttpClient httpClient, ManticoreConnectionOptions options)
    {
        _client = client;
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri(options.HttpEndpoint);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<(List<long>, int)> GetNoteIdsByKeywordAsync(long userId, string query, int pageNumber, int pageSize, NoteFilterType filter = NoteFilterType.Normal)
    {
        query = query?.Trim() ?? string.Empty;
        if (query.Length == 0)
            return (new List<long>(), 0);

        // Bigram indexing requires at least 2 characters to generate valid tokens
        if (query.Length == 1)
            return (new List<long>(), 0);

        var queryObject = _BuildNoteSearchQuery(userId, query, filter, pageSize, pageNumber);

        var content = new StringContent(JsonSerializer.Serialize(queryObject, JsonSerializerConfig.Default), Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync("json/search", content);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            // Log the error response for debugging
            Console.WriteLine("ManticoreSearch Error: " + errorContent);
            throw new Exception($"ManticoreSearch returned error: {response.StatusCode} - {errorContent}");
        }

        var responseContent = await response.Content.ReadAsStringAsync();
        // Log the raw response for debugging
        Console.WriteLine("ManticoreSearch Response: " + responseContent);

        var searchResult = JsonSerializer.Deserialize<ManticoreSearchResult>(responseContent, JsonSerializerConfig.Default);

        var total = searchResult?.hits?.total ?? 0;
        var noteIdList = new List<long>();
        if (searchResult?.hits?.hits != null)
        {
            foreach (var hit in searchResult.hits.hits)
            {
                if (hit?._source != null)
                {
                    noteIdList.Add(hit._id);
                }
            }
        }

        return (noteIdList, (int)total);
    }

    public async Task SyncNoteToIndexAsync(Note note, string fullContent, float[]? embedding = null)
    {
        var doc = new Dictionary<string, object>
        {
            ["userid"] = note.UserId,
            ["islong"] = note.IsLong ? 1 : 0,
            ["isprivate"] = note.IsPrivate ? 1 : 0,
            ["ismarkdown"] = note.IsMarkdown ? 1 : 0,
            ["content"] = fullContent,
            ["tags"] = string.Join(" ", fullContent.GetTags()),
            ["createdat"] = note.CreatedAt,
            ["updatedat"] = note.UpdatedAt ?? 0,
            ["deletedat"] = note.DeletedAt ?? 0
        };

        // The vector rides in the same REPLACE as the content. REPLACE rewrites the whole doc, so a note's
        // vector is only preserved when the writer that rewrites the content also supplies the vector.
        if (embedding != null)
        {
            doc["embedding"] = embedding;
        }

        var requestBody = new { index = "noteindex", id = note.Id, doc };

        var content = new StringContent(JsonSerializer.Serialize(requestBody, JsonSerializerConfig.Default), Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync("json/replace", content);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new Exception($"ManticoreSearch replace operation failed: {response.StatusCode} - {errorContent}");
        }
    }

    public async Task<List<long>> GetSemanticNoteIdsAsync(long userId, float[] queryVector, NoteFilterType filter, int k, double maxDistance = 0)
    {
        if (queryVector.Length == 0 || k <= 0)
            return new List<long>();

        // knn.query carries the search vector; the sibling top-level "query" carries the owner/delete-state
        // filter (identical clauses to the keyword path) so vector search can never leak another user's or a
        // soft-deleted note. See https://manual.manticoresearch.com/Searching/KNN (filter via sibling query).
        var queryObject = new Dictionary<string, object>
        {
            { "table", "noteindex" },
            {
                "knn", new Dictionary<string, object>
                {
                    { "field", "embedding" },
                    { "query", queryVector },
                    { "k", k }
                }
            },
            {
                "query", new Dictionary<string, object>
                {
                    { "bool", new Dictionary<string, object> { { "must", _BuildOwnerFilterClauses(userId, filter) } } }
                }
            },
            { "limit", k },
            { "_source", new[] { "id" } }
        };

        var content = new StringContent(JsonSerializer.Serialize(queryObject, JsonSerializerConfig.Default), Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync("json/search", content);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new Exception($"ManticoreSearch KNN search failed: {response.StatusCode} - {errorContent}");
        }

        var responseContent = await response.Content.ReadAsStringAsync();
        var searchResult = JsonSerializer.Deserialize<ManticoreSearchResult>(responseContent, JsonSerializerConfig.Default);

        var noteIdList = new List<long>();
        if (searchResult?.hits?.hits != null)
        {
            foreach (var hit in searchResult.hits.hits)
            {
                if (hit?._source == null)
                    continue;
                // Hits arrive nearest-first; apply the optional distance ceiling.
                if (maxDistance > 0 && hit._knn_dist > maxDistance)
                    continue;
                noteIdList.Add(hit._id);
            }
        }

        return noteIdList;
    }

    public async Task DeleteNoteFromIndexAsync(long id)
    {
        var updateData = new
        {
            index = "noteindex",
            id = id,
            doc = new
            {
                deletedat = DateTime.Now.ToUnixTimeSeconds()
            }
        };

        var content = new StringContent(JsonSerializer.Serialize(updateData, JsonSerializerConfig.Default), Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync("json/update", content);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new Exception($"ManticoreSearch delete operation failed: {response.StatusCode} - {errorContent}");
        }
    }

    public async Task UndeleteNoteFromIndexAsync(long id)
    {
        var updateData = new
        {
            index = "noteindex",
            id = id,
            doc = new
            {
                deletedat = 0
            }
        };

        var content = new StringContent(JsonSerializer.Serialize(updateData, JsonSerializerConfig.Default), Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync("json/update", content);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new Exception($"ManticoreSearch undelete operation failed: {response.StatusCode} - {errorContent}");
        }
    }

    public async Task PurgeUserDeletedNotesFromIndexAsync(long userId)
    {
        var deleteQuery = new
        {
            index = "noteindex",
            query = new
            {
                @bool = new
                {
                    must = new object[]
                    {
                        new { equals = new Dictionary<string, long> { { "UserId", userId } } },
                        new { range = new { deletedat = new { gt = 0 } } }
                    }
                }
            }
        };

        var content = new StringContent(JsonSerializer.Serialize(deleteQuery, JsonSerializerConfig.Default), Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync("json/delete", content);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new Exception($"ManticoreSearch purge operation failed: {response.StatusCode} - {errorContent}");
        }
    }

    private static Dictionary<string, object> _BuildNoteSearchQuery(long userId, string query, NoteFilterType filter, int pageSize, int pageNumber)
    {
        var mustClauses = new List<object>
        {
            new Dictionary<string, object> // Query for content: prioritize exact phrase match
            {
                {
                    "bool", new Dictionary<string, object>
                    {
                        {
                            "should", new List<object>
                            {
                                new Dictionary<string, object> // Boosted exact phrase match
                                {
                                    { "match_phrase", new Dictionary<string, object>
                                        {
                                            { "Content", new Dictionary<string, object>
                                                {
                                                    { "query", query },
                                                    { "boost", 2.0f } // Adjust boost factor as needed
                                                }
                                            }
                                        }
                                    }
                                },
                                new Dictionary<string, object> // AND match: all tokens must appear
                                {
                                    { "match", new Dictionary<string, object>
                                        {
                                            { "Content", new Dictionary<string, object>
                                                {
                                                    { "query", query },
                                                    { "operator", "and" }
                                                }
                                            }
                                        }
                                    }
                                },
                                new Dictionary<string, object> // AND match in tags: all tokens must appear
                                {
                                    { "match", new Dictionary<string, object>
                                        {
                                            { "Tags", new Dictionary<string, object>
                                                {
                                                    { "query", query },
                                                    { "operator", "and" }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        },
                        { "minimum_should_match", 1 } // At least one of the "should" clauses must match
                    }
                }
            }
        };
        // Owner + delete-state isolation is shared verbatim with the KNN path via _BuildOwnerFilterClauses.
        mustClauses.AddRange(_BuildOwnerFilterClauses(userId, filter));
        var source = new[] { "id" };

        return new Dictionary<string, object>
        {
            { "table", "noteindex" },
            { "track_scores", true },
            { "query", new Dictionary<string, object>
                {
                    { "bool", new Dictionary<string, object>
                        {
                            { "must", mustClauses }
                        }
                    }
                }
            },
            { "limit", pageSize },
            { "offset", (pageNumber - 1) * pageSize },
            { "sort", new List<object> // Sort by score (relevance) first, then by creation date
                {
                    new Dictionary<string, string> { { "_score", "desc" } },
                    new Dictionary<string, string> { { "CreatedAt", "desc" } }
                }
            },
            {"_source", source}
        };
    }

    /// <summary>
    /// Owner + delete-state isolation clauses, shared by the keyword and KNN search paths so vector
    /// search enforces exactly the same access boundary: own notes only (private included), and either
    /// non-deleted (Normal) or deleted-only (Deleted) — never another user's or a soft-deleted note.
    /// </summary>
    private static List<object> _BuildOwnerFilterClauses(long userId, NoteFilterType filter)
    {
        var clauses = new List<object>
        {
            new Dictionary<string, object> { { "equals", new Dictionary<string, long> { { "UserId", userId } } } }
        };

        if (filter == NoteFilterType.Normal)
        {
            clauses.Add(new Dictionary<string, object> { { "equals", new Dictionary<string, long> { { "DeletedAt", 0 } } } });
        }
        else
        {
            clauses.Add(new Dictionary<string, object> { { "range", new Dictionary<string, object> { { "DeletedAt", new Dictionary<string, long> { { "gt", 0 } } } } } });
        }

        return clauses;
    }
}
