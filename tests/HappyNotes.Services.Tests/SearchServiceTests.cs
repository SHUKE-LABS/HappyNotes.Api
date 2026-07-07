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
