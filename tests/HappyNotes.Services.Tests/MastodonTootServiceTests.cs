using System.Net;
using HappyNotes.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace HappyNotes.Services.Tests;

public class MastodonTootServiceTests
{
    private Mock<ILogger<MastodonTootService>> _mockLogger;
    private Mock<IHttpClientFactory> _mockHttpClientFactory;
    private MastodonTootService _service;

    [SetUp]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<MastodonTootService>>();
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _service = new MastodonTootService(_mockLogger.Object, _mockHttpClientFactory.Object);
    }

    [Test]
    public async Task SendTootAsync_PartialImageUploadFailure_EmitsWarningWithNoteIdUserIdAndCounts()
    {
        // Arrange: both image downloads fail, triggering the partial-failure branch
        var failingHandler = new FailingHttpMessageHandler();
        _mockHttpClientFactory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(failingHandler));

        const long noteId = 42L;
        const long userId = 7L;
        const string markdownContent = "Text\n![alt1](http://img.example.com/1.png)\n![alt2](http://img.example.com/2.png)";

        // Act: SendTootAsync will throw after emitting the warning (MastodonClient has no real endpoint)
        try
        {
            await _service.SendTootAsync(
                "https://mastodon.example.com",
                "fake-token",
                markdownContent,
                isPrivate: false,
                isMarkdown: true,
                noteId: noteId,
                userId: userId);
        }
        catch
        {
            // expected: MastodonClient.PublishStatus has no real server — the warning was already emitted
        }

        // Assert: LogWarning was called with the right noteId, userId, and failure fields
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) =>
                    v.ToString()!.Contains($"note {noteId}") &&
                    v.ToString()!.Contains($"user {userId}") &&
                    v.ToString()!.Contains("2/2 images failed")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    private sealed class FailingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
