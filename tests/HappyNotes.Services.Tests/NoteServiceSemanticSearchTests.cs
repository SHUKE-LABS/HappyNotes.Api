using Api.Framework;
using Api.Framework.Models;
using AutoMapper;
using HappyNotes.Common.Enums;
using HappyNotes.Entities;
using HappyNotes.Repositories.interfaces;
using HappyNotes.Services;
using HappyNotes.Services.interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace HappyNotes.Services.Tests;

/// <summary>
/// Covers the semantic merge in NoteService.SearchUserNotes: keyword-first ordering, dedup of semantic
/// candidates against keyword hits, paging across the merged virtual list, and keyword-only fallback.
/// </summary>
[TestFixture]
public class NoteServiceSemanticSearchTests
{
    private Mock<ISearchService> _searchService = null!;
    private Mock<IEmbeddingService> _embeddingService = null!;
    private Mock<INoteRepository> _noteRepository = null!;
    private SemanticSearchOptions _options = null!;
    private NoteService _noteService = null!;

    private const long UserId = 42;
    private const string Keyword = "hello world";

    [SetUp]
    public void Setup()
    {
        _searchService = new Mock<ISearchService>();
        _embeddingService = new Mock<IEmbeddingService>();
        _noteRepository = new Mock<INoteRepository>();
        _options = new SemanticSearchOptions { Enabled = true, TopK = 50, KeywordMergeCap = 1000, MaxDistance = 0 };

        // Return notes in reversed order to prove the merged ordering is re-applied after the SQL IN fetch.
        _noteRepository.Setup(r => r.GetListByIdsAsync(It.IsAny<long[]>()))
            .ReturnsAsync((long[] ids) => ids.Reverse().Select(id => new Note { Id = id }).ToList());

        _noteService = new NoteService(
            _searchService.Object,
            _embeddingService.Object,
            _options,
            new[] { new Mock<ISyncNoteService>().Object },
            new Mock<INoteTagService>().Object,
            _noteRepository.Object,
            new Mock<IRepositoryBase<LongNote>>().Object,
            new Mock<IMapper>().Object,
            new Mock<ILogger<NoteService>>().Object,
            new FakeTimeProvider());
    }

    private void SetupEmbedding() =>
        _embeddingService.Setup(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { 0.1f, 0.2f });

    private void SetupSemantic(params long[] ids) =>
        _searchService.Setup(s => s.GetSemanticNoteIdsAsync(UserId, It.IsAny<float[]>(), It.IsAny<NoteFilterType>(), It.IsAny<int>(), It.IsAny<double>()))
            .ReturnsAsync(ids.ToList());

    private void SetupKeyword(int pageNumber, int pageSize, List<long> ids, int total) =>
        _searchService.Setup(s => s.GetNoteIdsByKeywordAsync(UserId, Keyword, pageNumber, pageSize, It.IsAny<NoteFilterType>()))
            .ReturnsAsync((ids, total));

    private static List<long> Ids(PageData<Note> page) => page.DataList.Select(n => n.Id).ToList();

    [Test]
    public async Task Disabled_UsesKeywordOnly_AndDoesNotEmbed()
    {
        _options.Enabled = false;
        SetupKeyword(1, 10, new List<long> { 1, 2 }, 2);

        var result = await _noteService.SearchUserNotes(UserId, 10, 1, Keyword);

        Assert.That(result.TotalCount, Is.EqualTo(2));
        CollectionAssert.AreEqual(new List<long> { 1, 2 }, Ids(result));
        _embeddingService.Verify(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _searchService.Verify(s => s.GetSemanticNoteIdsAsync(It.IsAny<long>(), It.IsAny<float[]>(), It.IsAny<NoteFilterType>(), It.IsAny<int>(), It.IsAny<double>()), Times.Never);
    }

    [Test]
    public async Task EmbeddingUnavailable_FallsBackToKeywordOnly()
    {
        _embeddingService.Setup(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((float[]?)null);
        SetupKeyword(1, 10, new List<long> { 1, 2 }, 2);

        var result = await _noteService.SearchUserNotes(UserId, 10, 1, Keyword);

        Assert.That(result.TotalCount, Is.EqualTo(2));
        CollectionAssert.AreEqual(new List<long> { 1, 2 }, Ids(result));
        _searchService.Verify(s => s.GetSemanticNoteIdsAsync(It.IsAny<long>(), It.IsAny<float[]>(), It.IsAny<NoteFilterType>(), It.IsAny<int>(), It.IsAny<double>()), Times.Never);
    }

    [Test]
    public async Task Merge_KeywordFirstThenSemantic_DedupsAndOrders()
    {
        SetupEmbedding();
        SetupSemantic(3, 4, 5);                       // 3 duplicates a keyword hit -> extras [4,5]
        SetupKeyword(1, _options.KeywordMergeCap, new List<long> { 1, 2, 3 }, 3); // capped set + real total
        SetupKeyword(1, 10, new List<long> { 1, 2, 3 }, 3);                        // this-page keyword slice

        var result = await _noteService.SearchUserNotes(UserId, 10, 1, Keyword);

        Assert.That(result.TotalCount, Is.EqualTo(5), "total = keyword hits + deduped semantic extras");
        CollectionAssert.AreEqual(new List<long> { 1, 2, 3, 4, 5 }, Ids(result));
    }

    [Test]
    public async Task Merge_PagesAcrossKeywordAndSemanticTail_LaterPagesNotEmpty()
    {
        // keyword hits: [1,2,3] (total 3); semantic extras after dedup: [4,5,6,7]; page size 2.
        SetupEmbedding();
        SetupSemantic(4, 5, 6, 7);
        SetupKeyword(1, _options.KeywordMergeCap, new List<long> { 1, 2, 3 }, 3);
        SetupKeyword(2, 2, new List<long> { 3 }, 3);   // deep page beyond capped-slice window -> direct query

        var page1 = await _noteService.SearchUserNotes(UserId, 2, 1, Keyword);
        var page2 = await _noteService.SearchUserNotes(UserId, 2, 2, Keyword);
        var page3 = await _noteService.SearchUserNotes(UserId, 2, 3, Keyword);
        var page4 = await _noteService.SearchUserNotes(UserId, 2, 4, Keyword);

        Assert.That(page1.TotalCount, Is.EqualTo(7));
        CollectionAssert.AreEqual(new List<long> { 1, 2 }, Ids(page1));   // pure keyword
        CollectionAssert.AreEqual(new List<long> { 3, 4 }, Ids(page2));   // straddle: keyword tail + semantic head
        CollectionAssert.AreEqual(new List<long> { 5, 6 }, Ids(page3));   // pure semantic
        CollectionAssert.AreEqual(new List<long> { 7 }, Ids(page4));      // semantic tail, not empty
    }
}
