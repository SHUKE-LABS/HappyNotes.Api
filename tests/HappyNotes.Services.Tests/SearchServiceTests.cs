using System.Net;
using System.Text.Json;
using HappyNotes.Common.Enums;
using HappyNotes.Entities;
using HappyNotes.Services.interfaces;
using Moq;
using Moq.Protected;
using SqlSugar;

namespace HappyNotes.Services.Tests;

[TestFixture]
public class SearchServiceTests
{
    private readonly Mock<IDatabaseClient> _mockDatabaseClient;
    private readonly SearchService _searchService;
    private readonly Mock<HttpMessageHandler> _mockHandler;

    public SearchServiceTests()
    {
        _mockDatabaseClient = new Mock<IDatabaseClient>();
        _mockHandler = new Mock<HttpMessageHandler>();
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"took\":0,\"timed_out\":false,\"hits\":{\"total\":0,\"hits\":[]}}")
            });
        var httpClient = new HttpClient(_mockHandler.Object);
        var options = new ManticoreConnectionOptions { HttpEndpoint = "http://127.0.0.1:9308" };
        _searchService = new SearchService(_mockDatabaseClient.Object, httpClient, options);
    }

    [SetUp]
    public void Setup()
    {
        // Reset mock invocations before each test to avoid cross-test interference.
        _mockDatabaseClient.Reset();
        _mockHandler.Reset();
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"took\":0,\"timed_out\":false,\"hits\":{\"total\":0,\"hits\":[]}}")
            });
    }

    [Test]
    public async Task SearchNotesAsync_NormalFilter_ReturnsPaginatedResults()
    {
        // Arrange
        long userId = 1;
        string query = "test";
        int pageNumber = 1;
        int pageSize = 10;
        NoteFilterType filter = NoteFilterType.Normal;
        var expectedNoteIds = new List<long> { 1, 2 };

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"took\":0,\"timed_out\":false,\"hits\":{\"total\":2,\"hits\":[{\"_id\":1,\"_source\":{\"id\":1,\"userid\":1,\"content\":\"Test note 1\"}},{\"_id\":2,\"_source\":{\"id\":2,\"userid\":1,\"content\":\"Test note 2\"}}]}}")
            });

        // Act
        var result = await _searchService.GetNoteIdsByKeywordAsync(userId, query, pageNumber, pageSize, filter);

        // Assert
        Assert.IsNotNull(result);
        Assert.That(result.Item2, Is.EqualTo(2));
        CollectionAssert.AreEqual(new List<long> { 1, 2 }, result.Item1);
    }

    [Test]
    public async Task SearchNotesAsync_DeletedFilter_ReturnsDeletedNotes()
    {
        // Arrange
        long userId = 1;
        string query = "deleted note";
        int pageNumber = 1;
        int pageSize = 10;
        NoteFilterType filter = NoteFilterType.Deleted;
        var expectedNoteIds = new List<long> { 3 };

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"took\":0,\"timed_out\":false,\"hits\":{\"total\":1,\"hits\":[{\"_id\":3,\"_source\":{\"id\":3,\"userid\":1,\"content\":\"Deleted note 1\"}}]}}")
            });

        // Act
        var result = await _searchService.GetNoteIdsByKeywordAsync(userId, query, pageNumber, pageSize, filter);

        // Assert
        Assert.IsNotNull(result);
        Assert.That(result.Item2, Is.EqualTo(1));
        CollectionAssert.AreEqual(new List<long> { 3 }, result.Item1);
    }

    [Test]
    public async Task SearchNotesAsync_EmptyResults_ReturnsEmptyPageData()
    {
        // Arrange
        long userId = 1;
        string query = "nonexistent";
        int pageNumber = 1;
        int pageSize = 10;
        NoteFilterType filter = NoteFilterType.Normal;
        var expectedNoteIds = new List<long>();

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"took\":0,\"timed_out\":false,\"hits\":{\"total\":0,\"hits\":[]}}")
            });

        // Act
        var result = await _searchService.GetNoteIdsByKeywordAsync(userId, query, pageNumber, pageSize, filter);

        // Assert
        Assert.IsNotNull(result);
        CollectionAssert.IsEmpty(result.Item1);
        Assert.That(result.Item2, Is.EqualTo(0));
    }

    [Test]
    public async Task SyncNoteToIndexAsync_SuccessfulInsertion_DoesNotThrow()
    {
        // Arrange
        var note = new Note
        {
            Id = 1,
            UserId = 1,
            Content = "Test content",
            CreatedAt = 1625097600,
        };
        string fullContent = "Test content";

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Post &&
                    req.RequestUri.ToString().Contains("json/replace")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"result\":\"success\"}")
            });

        // Act
        await _searchService.SyncNoteToIndexAsync(note, fullContent);

        // Assert
        _mockHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Post &&
                req.RequestUri.ToString().Contains("json/replace")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Test]
    public async Task SyncNoteToIndexAsync_SpecialCharacters_HandlesEscaping()
    {
        // Arrange
        var note = new Note
        {
            Id = 2,
            UserId = 1,
            Content = @"Content with 'quotes' and \slashes\",
            CreatedAt = 1625097600,
        };
        string fullContent = @"Content with 'quotes' and \slashes\";

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Post &&
                    req.RequestUri.ToString().Contains("json/replace")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"result\":\"success\"}")
            });

        // Act
        await _searchService.SyncNoteToIndexAsync(note, fullContent);

        // Assert - Verify HTTP API call was made
        _mockHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Post &&
                req.RequestUri.ToString().Contains("json/replace")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Test]
    public async Task DeleteNoteFromIndexAsync_ExistingNote_UpdatesDeletedAt()
    {
        // Arrange
        long noteId = 1;

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Post &&
                    req.RequestUri.ToString().Contains("json/update")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"result\":\"success\"}")
            });

        // Act
        await _searchService.DeleteNoteFromIndexAsync(noteId);

        // Assert
        _mockHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Post &&
                req.RequestUri.ToString().Contains("json/update")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Test]
    public async Task UndeleteNoteFromIndexAsync_DeletedNote_ResetsDeletedAt()
    {
        // Arrange
        long noteId = 1;

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Post &&
                    req.RequestUri.ToString().Contains("json/update")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"result\":\"success\"}")
            });

        // Act
        await _searchService.UndeleteNoteFromIndexAsync(noteId);

        // Assert
        _mockHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Post &&
                req.RequestUri.ToString().Contains("json/update")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Test]
    public async Task GetNoteIdsByKeywordAsync_MultiTokenQuery_SendsAndOperatorInMatchClauses()
    {
        // Regression test for phrase-matching fix: both Content and Tags "match" clauses must use
        // operator:"and" so that multi-token CJK queries (e.g. "小菜园 浇水") require ALL
        // tokens to appear rather than any one of them.
        HttpRequestMessage? captured = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"took\":0,\"timed_out\":false,\"hits\":{\"total\":0,\"hits\":[]}}")
            });

        await _searchService.GetNoteIdsByKeywordAsync(1, "小菜园 浇水", 1, 10, NoteFilterType.Normal);

        Assert.IsNotNull(captured);
        var body = await captured!.Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        // Walk all must clauses looking for the bool/should block (position may change as code evolves)
        var must = doc.RootElement
            .GetProperty("query")
            .GetProperty("bool")
            .GetProperty("must");

        string? contentOperator = null;
        string? tagsOperator = null;
        foreach (var mustClause in must.EnumerateArray())
        {
            if (!mustClause.TryGetProperty("bool", out var boolClause)) continue;
            if (!boolClause.TryGetProperty("should", out var should)) continue;
            foreach (var shouldClause in should.EnumerateArray())
            {
                if (!shouldClause.TryGetProperty("match", out var match)) continue;
                if (match.TryGetProperty("Content", out var contentClause) &&
                    contentClause.TryGetProperty("operator", out var op))
                    contentOperator = op.GetString();
                if (match.TryGetProperty("Tags", out var tagsClause) &&
                    tagsClause.TryGetProperty("operator", out var tagsOp))
                    tagsOperator = tagsOp.GetString();
            }
        }

        Assert.That(contentOperator, Is.EqualTo("and"),
            "Content match clause must use operator:\"and\" to prevent OR-token relevance degradation");
        Assert.That(tagsOperator, Is.EqualTo("and"),
            "Tags match clause must also use operator:\"and\" for consistency with the Content fix");
    }

    [Test]
    public async Task GetSemanticNoteIdsAsync_AppliesOwnerAndDeleteFilters_AndReturnsIdsInOrder()
    {
        HttpRequestMessage? captured = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"hits\":{\"total\":2,\"hits\":[{\"_id\":7,\"_knn_dist\":0.10,\"_source\":{\"id\":7}},{\"_id\":9,\"_knn_dist\":0.20,\"_source\":{\"id\":9}}]}}")
            });

        var result = await _searchService.GetSemanticNoteIdsAsync(42, new[] { 0.1f, 0.2f, 0.3f }, NoteFilterType.Normal, 10);

        CollectionAssert.AreEqual(new List<long> { 7, 9 }, result);

        Assert.IsNotNull(captured);
        var body = await captured!.Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // knn block carries the vector and field
        var knn = root.GetProperty("knn");
        Assert.That(knn.GetProperty("field").GetString(), Is.EqualTo("embedding"));
        Assert.That(knn.GetProperty("query").GetArrayLength(), Is.EqualTo(3));

        // sibling top-level query carries the identical owner + delete-state isolation as the keyword path
        var must = root.GetProperty("query").GetProperty("bool").GetProperty("must");
        long? userIdFilter = null;
        long? deletedAtFilter = null;
        foreach (var clause in must.EnumerateArray())
        {
            if (clause.TryGetProperty("equals", out var eq))
            {
                if (eq.TryGetProperty("UserId", out var uid)) userIdFilter = uid.GetInt64();
                if (eq.TryGetProperty("DeletedAt", out var del)) deletedAtFilter = del.GetInt64();
            }
        }
        Assert.That(userIdFilter, Is.EqualTo(42), "KNN query must filter by owner UserId");
        Assert.That(deletedAtFilter, Is.EqualTo(0), "Normal filter must exclude soft-deleted notes in the KNN query");
    }

    [Test]
    public async Task GetSemanticNoteIdsAsync_DeletedFilter_UsesDeletedAtRange()
    {
        HttpRequestMessage? captured = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"hits\":{\"total\":0,\"hits\":[]}}")
            });

        await _searchService.GetSemanticNoteIdsAsync(1, new[] { 0.1f }, NoteFilterType.Deleted, 5);

        var body = await captured!.Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var must = doc.RootElement.GetProperty("query").GetProperty("bool").GetProperty("must");
        var hasDeletedRange = false;
        foreach (var clause in must.EnumerateArray())
        {
            if (clause.TryGetProperty("range", out var range) && range.TryGetProperty("DeletedAt", out var del)
                && del.TryGetProperty("gt", out var gt) && gt.GetInt64() == 0)
            {
                hasDeletedRange = true;
            }
        }
        Assert.That(hasDeletedRange, Is.True, "Deleted filter must select DeletedAt > 0 in the KNN query");
    }

    [Test]
    public async Task GetSemanticNoteIdsAsync_MaxDistance_DropsFartherCandidates()
    {
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"hits\":{\"total\":3,\"hits\":[{\"_id\":1,\"_knn_dist\":0.10,\"_source\":{\"id\":1}},{\"_id\":2,\"_knn_dist\":0.50,\"_source\":{\"id\":2}},{\"_id\":3,\"_knn_dist\":0.90,\"_source\":{\"id\":3}}]}}")
            });

        var result = await _searchService.GetSemanticNoteIdsAsync(1, new[] { 0.1f }, NoteFilterType.Normal, 10, maxDistance: 0.6);

        CollectionAssert.AreEqual(new List<long> { 1, 2 }, result);
    }

    [Test]
    public async Task SyncNoteToIndexAsync_WithEmbedding_IncludesEmbeddingInReplace()
    {
        HttpRequestMessage? captured = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"result\":\"success\"}")
            });

        var note = new Note { Id = 5, UserId = 1, Content = "hi", CreatedAt = 1 };
        await _searchService.SyncNoteToIndexAsync(note, "hi", new[] { 0.1f, 0.2f });

        var body = await captured!.Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var embedding = doc.RootElement.GetProperty("doc").GetProperty("embedding");
        Assert.That(embedding.GetArrayLength(), Is.EqualTo(2));
    }

    [Test]
    public async Task SyncNoteToIndexAsync_WithoutEmbedding_OmitsEmbeddingField()
    {
        HttpRequestMessage? captured = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"result\":\"success\"}")
            });

        var note = new Note { Id = 6, UserId = 1, Content = "hi", CreatedAt = 1 };
        await _searchService.SyncNoteToIndexAsync(note, "hi");

        var body = await captured!.Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.That(doc.RootElement.GetProperty("doc").TryGetProperty("embedding", out _), Is.False);
    }

    [Test]
    public async Task PurgeUserDeletedNotesFromIndexAsync_DeletedNotes_RemovesThem()
    {
        // Arrange
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Post &&
                    req.RequestUri.ToString().Contains("json/delete")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"deleted\":10}")
            });

        // Act
        await _searchService.PurgeUserDeletedNotesFromIndexAsync(123);

        // Assert
        _mockHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Post &&
                req.RequestUri.ToString().Contains("json/delete")),
            ItExpr.IsAny<CancellationToken>());
    }
}
