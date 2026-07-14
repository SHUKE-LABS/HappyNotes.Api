using System.Linq.Expressions;
using Api.Framework;
using HappyNotes.Entities;
using HappyNotes.Repositories.interfaces;
using HappyNotes.Services;
using HappyNotes.Services.interfaces;
using Microsoft.Extensions.Logging;
using Moq;

namespace HappyNotes.Services.Tests;

[TestFixture]
public class NoteVectorBackfillServiceTests
{
    private Mock<INoteRepository> _noteRepository = null!;
    private Mock<IRepositoryBase<LongNote>> _longNoteRepository = null!;
    private Mock<ISearchService> _searchService = null!;
    private Mock<IEmbeddingService> _embeddingService = null!;
    private SemanticSearchOptions _options = null!;
    private string _cursorPath = null!;

    [SetUp]
    public void Setup()
    {
        _noteRepository = new Mock<INoteRepository>();
        _longNoteRepository = new Mock<IRepositoryBase<LongNote>>();
        _searchService = new Mock<ISearchService>();
        _embeddingService = new Mock<IEmbeddingService>();
        _cursorPath = Path.Combine(Path.GetTempPath(), $"vector-backfill-test-{Guid.NewGuid():N}.cursor");
        _options = new SemanticSearchOptions { Enabled = true, Dimensions = 2, BackfillCursorPath = _cursorPath };
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_cursorPath)) File.Delete(_cursorPath);
    }

    private NoteVectorBackfillService CreateService() => new(
        _noteRepository.Object, _longNoteRepository.Object, _searchService.Object,
        _embeddingService.Object, _options, new Mock<ILogger<NoteVectorBackfillService>>().Object);

    private void SetupBatch(params Note[] notes) =>
        _noteRepository.Setup(r => r.GetTopListAsync(It.IsAny<int>(), It.IsAny<Expression<Func<Note, bool>>>(), It.IsAny<List<string>>()))
            .ReturnsAsync(notes.ToList());

    [Test]
    public async Task RunAsync_Disabled_DoesNothing()
    {
        _options.Enabled = false;
        var result = await CreateService().RunAsync();

        Assert.That(result.Completed, Is.False);
        Assert.That(result.Embedded, Is.EqualTo(0));
        _noteRepository.Verify(r => r.GetTopListAsync(It.IsAny<int>(), It.IsAny<Expression<Func<Note, bool>>>(), It.IsAny<List<string>>()), Times.Never);
    }

    [Test]
    public async Task RunAsync_EmbedsAllNotes_AndCompletes()
    {
        SetupBatch(new Note { Id = 1, Content = "a" }, new Note { Id = 2, Content = "b" });
        _embeddingService.Setup(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { 0.1f, 0.2f });

        var result = await CreateService().RunAsync();

        Assert.That(result.Completed, Is.True);
        Assert.That(result.Embedded, Is.EqualTo(2));
        Assert.That(result.LastProcessedId, Is.EqualTo(2));
        _searchService.Verify(s => s.SyncNoteToIndexAsync(It.IsAny<Note>(), It.IsAny<string>(), It.IsAny<float[]>()), Times.Exactly(2));
        Assert.That(File.ReadAllText(_cursorPath).Trim(), Is.EqualTo("2"));
    }

    [Test]
    public async Task RunAsync_StopsAndKeepsCursor_WhenBackendUnavailable()
    {
        SetupBatch(new Note { Id = 1, Content = "a" }, new Note { Id = 2, Content = "b" });
        _embeddingService.Setup(e => e.EmbedAsync("a", It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 0.1f, 0.2f });
        _embeddingService.Setup(e => e.EmbedAsync("b", It.IsAny<CancellationToken>())).ReturnsAsync((float[]?)null);

        var result = await CreateService().RunAsync();

        Assert.That(result.Completed, Is.False, "run stops when the backend returns null for a real note");
        Assert.That(result.Embedded, Is.EqualTo(1));
        Assert.That(result.LastProcessedId, Is.EqualTo(1), "cursor stays at the last success so the failed note is retried on resume");
        Assert.That(File.ReadAllText(_cursorPath).Trim(), Is.EqualTo("1"));
    }

    [Test]
    public async Task RunAsync_SkipsEmptyContent_WithoutEmbedding()
    {
        SetupBatch(new Note { Id = 1, Content = "   " });

        var result = await CreateService().RunAsync();

        Assert.That(result.Completed, Is.True);
        Assert.That(result.Skipped, Is.EqualTo(1));
        Assert.That(result.Embedded, Is.EqualTo(0));
        _embeddingService.Verify(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
