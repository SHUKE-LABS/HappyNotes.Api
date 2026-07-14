using System.Net;
using HappyNotes.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;

namespace HappyNotes.Services.Tests;

[TestFixture]
public class EmbeddingServiceTests
{
    private Mock<HttpMessageHandler> _mockHandler = null!;
    private Mock<ILogger<EmbeddingService>> _mockLogger = null!;

    [SetUp]
    public void Setup()
    {
        _mockHandler = new Mock<HttpMessageHandler>();
        _mockLogger = new Mock<ILogger<EmbeddingService>>();
    }

    private EmbeddingService CreateService(SemanticSearchOptions options)
    {
        var httpClient = new HttpClient(_mockHandler.Object) { BaseAddress = new Uri("http://127.0.0.1:11434") };
        return new EmbeddingService(httpClient, options, _mockLogger.Object);
    }

    private void SetupResponse(HttpStatusCode status, string content)
    {
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage { StatusCode = status, Content = new StringContent(content) });
    }

    [Test]
    public async Task EmbedAsync_WhenDisabled_ReturnsNullWithoutCallingBackend()
    {
        var service = CreateService(new SemanticSearchOptions { Enabled = false, EmbeddingEndpoint = "api/embeddings" });

        var result = await service.EmbedAsync("hello");

        Assert.That(result, Is.Null);
        _mockHandler.Protected().Verify("SendAsync", Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Test]
    public async Task EmbedAsync_WhenEmptyText_ReturnsNull()
    {
        var service = CreateService(new SemanticSearchOptions { Enabled = true, EmbeddingEndpoint = "api/embeddings" });

        var result = await service.EmbedAsync("   ");

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task EmbedAsync_Success_ReturnsEmbedding()
    {
        SetupResponse(HttpStatusCode.OK, "{\"embedding\":[0.1,0.2,0.3]}");
        var service = CreateService(new SemanticSearchOptions { Enabled = true, EmbeddingEndpoint = "api/embeddings", Dimensions = 3 });

        var result = await service.EmbedAsync("hello");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Length, Is.EqualTo(3));
        Assert.That(result[0], Is.EqualTo(0.1f).Within(0.0001f));
    }

    [Test]
    public async Task EmbedAsync_DimensionMismatch_ReturnsNull()
    {
        SetupResponse(HttpStatusCode.OK, "{\"embedding\":[0.1,0.2,0.3]}");
        var service = CreateService(new SemanticSearchOptions { Enabled = true, EmbeddingEndpoint = "api/embeddings", Dimensions = 1024 });

        var result = await service.EmbedAsync("hello");

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task EmbedAsync_BackendError_ReturnsNull()
    {
        SetupResponse(HttpStatusCode.InternalServerError, "boom");
        var service = CreateService(new SemanticSearchOptions { Enabled = true, EmbeddingEndpoint = "api/embeddings", Dimensions = 3 });

        var result = await service.EmbedAsync("hello");

        Assert.That(result, Is.Null);
    }
}
