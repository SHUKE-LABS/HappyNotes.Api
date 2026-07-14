using HappyNotes.Common.Enums;
using HappyNotes.Entities;
using HappyNotes.Services;
using HappyNotes.Services.interfaces;
using Moq;

namespace HappyNotes.Services.Tests;

// Manual integration tests for semantic (KNN) search isolation. They require a running Manticore
// instance whose `noteindex` has the `Embedding float_vector` column (see docker/create_table.sql).
// Vectors are injected directly, so no embedding backend (Ollama) is needed to run these.
// HTTP JSON endpoint defaults to http://127.0.0.1:9308/.
[TestFixture]
[Explicit("Manual test - requires local Manticore with the Embedding float_vector column at http://127.0.0.1:9308/")]
public class SemanticSearchIntegrationTests
{
    private const int Dims = 1024;
    private SearchService _searchService = null!;

    // Two users; a deleted note for user A; distinct ids well above any real data to avoid collisions.
    private const long UserA = 900001;
    private const long UserB = 900002;
    private const long NoteA1 = 990001; // user A, active
    private const long NoteA2 = 990002; // user A, active
    private const long NoteADeleted = 990003; // user A, soft-deleted
    private const long NoteB1 = 990004; // user B, active

    [SetUp]
    public void Setup()
    {
        var options = new ManticoreConnectionOptions { HttpEndpoint = "http://127.0.0.1:9308/" };
        _searchService = new SearchService(new Mock<IDatabaseClient>().Object, new HttpClient(), options);
    }

    private static float[] Vector(float seed)
    {
        var v = new float[Dims];
        for (var i = 0; i < Dims; i++) v[i] = seed;
        return v;
    }

    private Note MakeNote(long id, long userId, long deletedAt) => new()
    {
        Id = id,
        UserId = userId,
        IsLong = false,
        IsPrivate = false,
        IsMarkdown = false,
        Content = $"note {id}",
        CreatedAt = 1_700_000_000,
        UpdatedAt = null,
        DeletedAt = deletedAt == 0 ? null : deletedAt
    };

    [Test]
    public async Task GetSemanticNoteIdsAsync_NeverReturnsOtherUsersOrDeletedNotes()
    {
        // Seed: all vectors identical so ranking cannot be the reason a note is excluded — only the filter can.
        await _searchService.SyncNoteToIndexAsync(MakeNote(NoteA1, UserA, 0), "note", Vector(0.5f));
        await _searchService.SyncNoteToIndexAsync(MakeNote(NoteA2, UserA, 0), "note", Vector(0.5f));
        await _searchService.SyncNoteToIndexAsync(MakeNote(NoteADeleted, UserA, 1_700_000_100), "note", Vector(0.5f));
        await _searchService.SyncNoteToIndexAsync(MakeNote(NoteB1, UserB, 0), "note", Vector(0.5f));

        // Give the RT index a moment to make the docs searchable.
        await Task.Delay(500);

        var results = await _searchService.GetSemanticNoteIdsAsync(UserA, Vector(0.5f), NoteFilterType.Normal, 50);

        Assert.That(results, Does.Contain(NoteA1));
        Assert.That(results, Does.Contain(NoteA2));
        Assert.That(results, Does.Not.Contain(NoteADeleted), "soft-deleted note must not appear under the Normal filter");
        Assert.That(results, Does.Not.Contain(NoteB1), "another user's note must never appear");
    }
}
