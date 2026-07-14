using Api.Framework;
using Api.Framework.Exceptions;
using Api.Framework.Extensions;
using Api.Framework.Models;
using AutoMapper;
using HappyNotes.Common;
using HappyNotes.Common.Enums;
using HappyNotes.Dto;
using HappyNotes.Entities;
using HappyNotes.Models;
using HappyNotes.Repositories.interfaces;
using HappyNotes.Services.interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using EventId = HappyNotes.Common.Enums.EventId;

namespace HappyNotes.Services.Tests;

[TestFixture]
public class NoteServiceTests
{
    private Mock<ISearchService> _searchService;
    private Mock<IEmbeddingService> _mockEmbeddingService;
    private SemanticSearchOptions _semanticSearchOptions;
    private Mock<ISyncNoteService> _mockSyncNoteService;
    private Mock<INoteTagService> _mockNoteTagService;
    private Mock<INoteRepository> _mockNoteRepository;
    private Mock<IRepositoryBase<LongNote>> _mockLongNoteRepository;
    private Mock<IMapper> _mockMapper;
    private Mock<ILogger<NoteService>> _mockLogger;
    private FakeTimeProvider _fakeTimeProvider;
    private NoteService _noteService;

    [SetUp]
    public void Setup()
    {
        _searchService = new Mock<ISearchService>();
        _mockEmbeddingService = new Mock<IEmbeddingService>();
        _semanticSearchOptions = new SemanticSearchOptions { Enabled = false };
        _mockSyncNoteService = new Mock<ISyncNoteService>();
        var syncServices = new[] { _mockSyncNoteService.Object };
        _mockNoteTagService = new Mock<INoteTagService>();
        _mockNoteRepository = new Mock<INoteRepository>();
        _mockLongNoteRepository = new Mock<IRepositoryBase<LongNote>>();
        _mockMapper = new Mock<IMapper>();
        _mockLogger = new Mock<ILogger<NoteService>>();
        _fakeTimeProvider = new FakeTimeProvider(new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero));
        _mockSyncNoteService.Setup(s => s.SyncNewNote(It.IsAny<Note>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _mockSyncNoteService.Setup(s => s.SyncEditNote(It.IsAny<Note>(), It.IsAny<string>(), It.IsAny<Note>()))
            .Returns(Task.CompletedTask);
        _mockSyncNoteService.Setup(s => s.SyncDeleteNote(It.IsAny<Note>()))
            .Returns(Task.CompletedTask);
        _mockSyncNoteService.Setup(s => s.SyncUndeleteNote(It.IsAny<Note>()))
            .Returns(Task.CompletedTask);

        _noteService = new NoteService(
            _searchService.Object,
            _mockEmbeddingService.Object,
            _semanticSearchOptions,
            syncServices,
            _mockNoteTagService.Object,
            _mockNoteRepository.Object,
            _mockLongNoteRepository.Object,
            _mockMapper.Object,
            _mockLogger.Object,
            _fakeTimeProvider
        );
    }

    [Test]
    public async Task Post_WithValidNote_ReturnsNoteId()
    {
        // Arrange
        var userId = 1L;
        var request = new PostNoteRequest { Content = "Test note" };
        var note = new Note
        {
            Id = 1,
            Content = "Test note",
            UserId = userId,
            CreatedAt = DateTime.UtcNow.ToUnixTimeSeconds()
        };

        _mockMapper.Setup(m => m.Map<PostNoteRequest, Note>(request))
            .Returns(note);
        _mockNoteRepository.Setup(r => r.InsertAsync(note))
            .ReturnsAsync(true);

        // Act
        var result = await _noteService.Post(userId, request);

        // Assert
        Assert.That(result, Is.EqualTo(note.Id));
        _mockNoteRepository.Verify(r => r.InsertAsync(It.IsAny<Note>()), Times.Once);
    }

    [Test]
    public void Post_WithEmptyContent_ThrowsArgumentException()
    {
        // Arrange
        var userId = 1L;
        var request = new PostNoteRequest { Content = "" };

        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
            await _noteService.Post(userId, request));
        Assert.That(ex.Message, Is.EqualTo("Nothing was submitted"));
    }

    [Test]
    public async Task Post_WhenSyncEnqueueFails_NoteStillPostedAndErrorLogged()
    {
        // Arrange
        var userId = 1L;
        var request = new PostNoteRequest { Content = "Test note" };
        var note = new Note { Id = 1, Content = "Test note", UserId = userId };

        _mockMapper.Setup(m => m.Map<PostNoteRequest, Note>(request)).Returns(note);
        _mockNoteRepository.Setup(r => r.InsertAsync(note)).ReturnsAsync(true);
        _mockSyncNoteService
            .Setup(s => s.SyncNewNote(It.IsAny<Note>(), It.IsAny<string>()))
            .ThrowsAsync(new Exception("Redis unavailable"));

        // Act — note operation must succeed despite sync failure
        var result = await _noteService.Post(userId, request);

        // Assert
        Assert.That(result, Is.EqualTo(note.Id));
        _mockLogger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<Microsoft.Extensions.Logging.EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains(note.Id.ToString())),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Test]
    public async Task Get_WithExistingPublicNote_ReturnsNote()
    {
        // Arrange
        var userId = 1L;
        var noteId = 1L;
        var note = new Note
        {
            Id = noteId,
            Content = "Test note",
            UserId = 2,
            IsPrivate = false,
            DeletedAt = null
        };

        _mockNoteRepository.Setup(r => r.Get(noteId))
            .ReturnsAsync(note);

        // Act
        var result = await _noteService.Get(userId, noteId);

        // Assert
        Assert.That(result, Is.EqualTo(note));
    }

    [Test]
    public void Get_WithNonExistentNote_ThrowsException()
    {
        // Arrange
        var userId = 1L;
        var noteId = 1L;
        _mockNoteRepository.Setup(r => r.Get(noteId))
            .ReturnsAsync((Note)null!);

        // Act & Assert
        var ex = Assert.ThrowsAsync<CustomException<object>>(async () =>
            await _noteService.Get(userId, noteId));
        Assert.That(ex.CustomData!.ErrorCode, Is.EqualTo((int)EventId._00100_NoteNotFound));
    }

    [Test]
    public void Get_WithPrivateNoteFromOtherUser_ThrowsException()
    {
        // Arrange
        var userId = 1L;
        var noteId = 1L;
        var note = new Note
        {
            Id = noteId,
            Content = "Private note",
            UserId = 2,
            IsPrivate = true
        };

        _mockNoteRepository.Setup(r => r.Get(noteId))
            .ReturnsAsync(note);

        // Act & Assert
        var ex = Assert.ThrowsAsync<CustomException<object>>(async () =>
            await _noteService.Get(userId, noteId));
        Assert.That(ex.CustomData!.ErrorCode, Is.EqualTo((int)EventId._00101_NoteIsPrivate));
    }

    [Test]
    public async Task Update_WithValidNote_ReturnsTrue()
    {
        // Arrange
        var userId = 1L;
        var noteId = 1L;
        var request = new PostNoteRequest { Content = "Updated content" };
        var existingNote = new Note
        {
            Id = noteId,
            Content = "Original content",
            UserId = userId
        };
        var updatedNote = new Note
        {
            Id = noteId,
            Content = "Updated content",
            UserId = userId
        };

        _mockNoteRepository.Setup(r => r.Get(noteId)).ReturnsAsync(existingNote);
        _mockMapper.Setup(m => m.Map<PostNoteRequest, Note>(request))
            .Returns(updatedNote);
        _mockNoteRepository.Setup(r => r.UpdateAsync(It.IsAny<Note>()))
            .ReturnsAsync(true);

        // Act
        var result = await _noteService.Update(userId, noteId, request);

        // Assert
        Assert.That(result, Is.True);
        _mockNoteRepository.Verify(r => r.UpdateAsync(It.IsAny<Note>()), Times.Once);
    }

    [Test]
    public async Task SetIsPrivate_WithOwnedNote_DelegatesToUpdateFlow()
    {
        // Arrange
        var userId = 1L;
        var noteId = 1L;
        var existingNote = new Note
        {
            Id = noteId,
            Content = "Original content",
            UserId = userId,
            IsPrivate = true,
            IsMarkdown = true
        };
        var updateRequest = new PostNoteRequest
        {
            Content = existingNote.Content,
            IsMarkdown = existingNote.IsMarkdown,
            IsPrivate = existingNote.IsPrivate
        };
        var updatedNote = new Note
        {
            Id = noteId,
            Content = existingNote.Content,
            UserId = userId,
            IsPrivate = false,
            IsMarkdown = true
        };

        _mockNoteRepository.SetupSequence(r => r.Get(noteId))
            .ReturnsAsync(existingNote)
            .ReturnsAsync(existingNote);
        _mockMapper.Setup(m => m.Map<PostNoteRequest>(existingNote)).Returns(updateRequest);
        _mockMapper.Setup(m => m.Map<PostNoteRequest, Note>(It.Is<PostNoteRequest>(r =>
                r.Content == existingNote.Content &&
                r.IsMarkdown == existingNote.IsMarkdown &&
                r.IsPrivate == false)))
            .Returns(updatedNote);
        _mockNoteRepository.Setup(r => r.UpdateAsync(It.IsAny<Note>())).ReturnsAsync(true);

        // Act
        var result = await _noteService.SetIsPrivate(userId, noteId, false);

        // Assert - database delegation only; sync fan-out is in SetIsPrivate_WhenPrivacyFlipped_FansOutToSyncWithNewValue
        Assert.That(result, Is.True);
        _mockNoteRepository.Verify(r => r.UpdateAsync(It.Is<Note>(n => n.Id == noteId && n.IsPrivate == false)), Times.Once);
    }

    [Test]
    public async Task SetIsPrivate_WhenPrivacyFlipped_FansOutToSyncWithNewValue()
    {
        // Arrange
        var userId = 1L;
        var noteId = 1L;
        var existingNote = new Note
        {
            Id = noteId,
            Content = "Original content",
            UserId = userId,
            IsPrivate = true,
            IsMarkdown = true
        };
        var updateRequest = new PostNoteRequest
        {
            Content = existingNote.Content,
            IsMarkdown = existingNote.IsMarkdown,
            IsPrivate = existingNote.IsPrivate
        };
        var updatedNote = new Note
        {
            Id = noteId,
            Content = existingNote.Content,
            UserId = userId,
            IsPrivate = false,
            IsMarkdown = true
        };

        _mockNoteRepository.SetupSequence(r => r.Get(noteId))
            .ReturnsAsync(existingNote)
            .ReturnsAsync(existingNote);
        _mockMapper.Setup(m => m.Map<PostNoteRequest>(existingNote)).Returns(updateRequest);
        _mockMapper.Setup(m => m.Map<PostNoteRequest, Note>(It.Is<PostNoteRequest>(r => r.IsPrivate == false)))
            .Returns(updatedNote);
        _mockNoteRepository.Setup(r => r.UpdateAsync(It.IsAny<Note>())).ReturnsAsync(true);

        // Act
        await _noteService.SetIsPrivate(userId, noteId, false);

        // Assert — sync is now awaited inline; verify directly
        _mockSyncNoteService.Verify(
            s => s.SyncEditNote(
                It.Is<Note>(n => n.IsPrivate == false),
                It.IsAny<string>(),
                It.Is<Note>(n => n.IsPrivate == true)),
            Times.Once);
    }

    [Test]
    public async Task SetIsPrivate_WithUnchangedValue_DoesNotUpdate()
    {
        // Arrange
        var userId = 1L;
        var noteId = 1L;
        var existingNote = new Note
        {
            Id = noteId,
            Content = "Original content",
            UserId = userId,
            IsPrivate = true
        };
        _mockNoteRepository.Setup(r => r.Get(noteId)).ReturnsAsync(existingNote);

        // Act
        var result = await _noteService.SetIsPrivate(userId, noteId, true);

        // Assert
        Assert.That(result, Is.True);
        _mockNoteRepository.Verify(r => r.UpdateAsync(It.IsAny<Note>()), Times.Never);
        // No fan-out on the no-op path: SetIsPrivate returns early before Update is reached,
        // so SyncEditNote must never be called.
        _mockSyncNoteService.Verify(
            s => s.SyncEditNote(It.IsAny<Note>(), It.IsAny<string>(), It.IsAny<Note>()),
            Times.Never);
    }

    [Test]
    public void SetIsPrivate_WithNotOwnedNote_ThrowsException()
    {
        // Arrange
        var userId = 1L;
        var noteId = 1L;
        var existingNote = new Note
        {
            Id = noteId,
            Content = "Original content",
            UserId = 2,
            IsPrivate = true
        };
        _mockNoteRepository.Setup(r => r.Get(noteId)).ReturnsAsync(existingNote);

        // Act & Assert
        var ex = Assert.ThrowsAsync<CustomException<object>>(async () =>
            await _noteService.SetIsPrivate(userId, noteId, false));
        Assert.That(ex.CustomData!.ErrorCode, Is.EqualTo((int)EventId._00102_NoteIsNotYours));
    }

    [Test]
    public async Task Delete_WithOwnedNote_ReturnsTrue()
    {
        // Arrange
        var userId = 1L;
        var noteId = 1L;
        var note = new Note
        {
            Id = noteId,
            Content = "Test note",
            UserId = userId,
            DeletedAt = null
        };

        _mockNoteRepository.Setup(r => r.GetFirstOrDefaultAsync(x => x.Id == noteId, null)).ReturnsAsync(note);
        _mockNoteRepository.Setup(r => r.UpdateAsync(It.IsAny<Note>()))
            .ReturnsAsync(true);

        // Act
        var result = await _noteService.Delete(userId, noteId);

        // Assert
        Assert.That(result, Is.True);
        Assert.That(note.DeletedAt, Is.Not.Null);
    }

    [Test]
    public async Task Undelete_WithDeletedOwnedNote_ReturnsTrue()
    {
        // Arrange
        var userId = 1L;
        var noteId = 1L;
        var note = new Note
        {
            Id = noteId,
            Content = "Test note",
            UserId = userId,
            DeletedAt = DateTime.UtcNow.ToUnixTimeSeconds()
        };

        var undeletedNote = new Note
        {
            Id = noteId,
            Content = "Test note",
            UserId = userId,
            DeletedAt = null // Undeleted note has DeletedAt reset to null
        };

        _mockNoteRepository.Setup(r => r.GetFirstOrDefaultAsync(x => x.Id == noteId, null)).ReturnsAsync(note);
        _mockNoteRepository.Setup(r => r.Get(noteId)).ReturnsAsync(undeletedNote);
        _mockNoteRepository.Setup(r => r.UndeleteAsync(noteId)).Returns(Task.CompletedTask);

        // Act
        var result = await _noteService.Undelete(userId, noteId);

        // Assert
        Assert.That(result, Is.True);
        _mockNoteRepository.Verify(r => r.UndeleteAsync(noteId), Times.Once);

        // Sync is now awaited inline; verify directly
        _mockSyncNoteService.Verify(s => s.SyncUndeleteNote(It.Is<Note>(n => n.Id == noteId && n.DeletedAt == null)), Times.Once);
    }

    [Test]
    public async Task GetUserNotes_WithValidParameters_ReturnsPageData()
    {
        // Arrange
        var userId = 1L;
        var pageSize = 10;
        var pageNumber = 1;
        var expectedNotes = new PageData<Note>
        {
            DataList = new List<Note> { new Note { Id = 1, Content = "Test" } },
            TotalCount = 1
        };

        _mockNoteRepository.Setup(r => r.GetUserNotes(userId, pageSize, pageNumber, false, false))
            .ReturnsAsync(expectedNotes);

        // Act
        var result = await _noteService.GetUserNotes(userId, pageSize, pageNumber);

        // Assert
        Assert.That(result, Is.EqualTo(expectedNotes));
    }

    [Test]
    public async Task GetPublicNotes_WithinMaxPage_ReturnsPageData()
    {
        // Arrange
        var pageSize = 10;
        var pageNumber = 1;
        var expectedNotes = new PageData<Note>
        {
            DataList = new List<Note> { new Note { Id = 1, Content = "Test" } },
            TotalCount = 1
        };

        _mockNoteRepository.Setup(r => r.GetPublicNotes(pageSize, pageNumber, false, It.IsAny<long?>()))
            .ReturnsAsync(expectedNotes);

        // Act
        var result = await _noteService.GetPublicNotes(pageSize, pageNumber);

        // Assert
        Assert.That(result, Is.EqualTo(expectedNotes));
    }

    [Test]
    public void GetPublicNotes_ExceedingMaxPage_ThrowsException()
    {
        // Arrange
        var pageSize = 10;
        var pageNumber = Constants.PublicNotesMaxPage + 1;

        // Act & Assert
        var ex = Assert.ThrowsAsync<Exception>(async () =>
            await _noteService.GetPublicNotes(pageSize, pageNumber));
        Assert.That(ex.Message, Does.Contain($"We only provide at most {Constants.PublicNotesMaxPage} page"));
    }

    [Test]
    public async Task SearchUserNotes_EmptyKeyword_ReturnsEmptyResults()
    {
        // Arrange
        var userId = 1L;
        var pageSize = 10;
        var pageNumber = 1;
        var keyword = "";

        _searchService.Setup(s => s.GetNoteIdsByKeywordAsync(userId, keyword, pageNumber, pageSize, NoteFilterType.Normal))
            .ReturnsAsync((new List<long>(), 0));
        _mockNoteRepository.Setup(r => r.GetListByIdsAsync(It.IsAny<long[]>()))
            .ReturnsAsync(new List<Note>());

        // Act
        var result = await _noteService.SearchUserNotes(userId, pageSize, pageNumber, keyword);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.DataList, Is.Empty);
        Assert.That(result.TotalCount, Is.EqualTo(0));
        Assert.That(result.PageIndex, Is.EqualTo(pageNumber));
        Assert.That(result.PageSize, Is.EqualTo(pageSize));
    }

    [Test]
    public async Task SearchUserNotes_SingleCharacterKeyword_ReturnsEmptyResults()
    {
        // Arrange
        var userId = 1L;
        var pageSize = 10;
        var pageNumber = 1;
        var keyword = "a";

        _searchService.Setup(s => s.GetNoteIdsByKeywordAsync(userId, keyword, pageNumber, pageSize, NoteFilterType.Normal))
            .ReturnsAsync((new List<long>(), 0));
        _mockNoteRepository.Setup(r => r.GetListByIdsAsync(It.IsAny<long[]>()))
            .ReturnsAsync(new List<Note>());

        // Act
        var result = await _noteService.SearchUserNotes(userId, pageSize, pageNumber, keyword);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.DataList, Is.Empty);
        Assert.That(result.TotalCount, Is.EqualTo(0));
        Assert.That(result.PageIndex, Is.EqualTo(pageNumber));
        Assert.That(result.PageSize, Is.EqualTo(pageSize));
    }

    [Test]
    public async Task SearchUserNotes_ValidKeyword_ReturnsSearchResults()
    {
        // Arrange
        var userId = 1L;
        var pageSize = 10;
        var pageNumber = 1;
        var keyword = "test";
        var searchResultNoteIds = new List<long> { 1, 2 };
        var searchTotal = 2;
        var notes = new List<Note>
        {
            new Note { Id = 1, Content = "Test note 1", UserId = userId },
            new Note { Id = 2, Content = "Test note 2", UserId = userId }
        };

        _searchService.Setup(s => s.GetNoteIdsByKeywordAsync(userId, keyword, pageNumber, pageSize, NoteFilterType.Normal))
            .ReturnsAsync((searchResultNoteIds, searchTotal));
        _mockNoteRepository.Setup(r => r.GetListByIdsAsync(It.IsAny<long[]>()))
            .ReturnsAsync(notes);

        // Act
        var result = await _noteService.SearchUserNotes(userId, pageSize, pageNumber, keyword);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.DataList.Count, Is.EqualTo(2));
        Assert.That(result.TotalCount, Is.EqualTo(searchTotal));
        Assert.That(result.PageIndex, Is.EqualTo(pageNumber));
        Assert.That(result.PageSize, Is.EqualTo(pageSize));
        CollectionAssert.AreEqual(notes, result.DataList);
    }












    [Test]
    public async Task SearchUserNotes_WithDeletedFilter_PassesFilterToSearchService()
    {
        // Arrange
        var userId = 1L;
        var pageSize = 10;
        var pageNumber = 1;
        var keyword = "test";
        var filter = NoteFilterType.Deleted;
        var searchResultNoteIds = new List<long> { 1 };
        var searchTotal = 1;
        var notes = new List<Note>
        {
            new Note { Id = 1, Content = "Deleted note", UserId = userId, DeletedAt = 123456 }
        };

        _searchService.Setup(s => s.GetNoteIdsByKeywordAsync(userId, keyword, pageNumber, pageSize, filter))
            .ReturnsAsync((searchResultNoteIds, searchTotal));
        _mockNoteRepository.Setup(r => r.GetListByIdsAsync(It.IsAny<long[]>()))
            .ReturnsAsync(notes);

        // Act
        var result = await _noteService.SearchUserNotes(userId, pageSize, pageNumber, keyword, filter);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.DataList.Count, Is.EqualTo(1));
        Assert.That(result.TotalCount, Is.EqualTo(searchTotal));
        _searchService.Verify(s => s.GetNoteIdsByKeywordAsync(userId, keyword, pageNumber, pageSize, filter), Times.Once);
    }

    [TestFixture]
    public class GetTimestampsTests
    {
        private Mock<ISearchService> _searchService;
        private Mock<IEmbeddingService> _mockEmbeddingService;
        private SemanticSearchOptions _semanticSearchOptions;
        private Mock<ISyncNoteService> _mockSyncNoteService;
        private Mock<INoteTagService> _mockNoteTagService;
        private Mock<INoteRepository> _mockNoteRepository;
        private Mock<IRepositoryBase<LongNote>> _mockLongNoteRepository;
        private Mock<IMapper> _mockMapper;
        private Mock<ILogger<NoteService>> _mockLogger;
        private FakeTimeProvider _fakeTimeProvider;
        private NoteService _noteService;

        [SetUp]
        public void Setup()
        {
            _searchService = new Mock<ISearchService>();
            _mockEmbeddingService = new Mock<IEmbeddingService>();
            _semanticSearchOptions = new SemanticSearchOptions { Enabled = false };
            _mockSyncNoteService = new Mock<ISyncNoteService>();
            var syncServices = new[] { _mockSyncNoteService.Object };
            _mockNoteTagService = new Mock<INoteTagService>();
            _mockNoteRepository = new Mock<INoteRepository>();
            _mockLongNoteRepository = new Mock<IRepositoryBase<LongNote>>();
            _mockMapper = new Mock<IMapper>();
            _mockLogger = new Mock<ILogger<NoteService>>();
            _fakeTimeProvider = new FakeTimeProvider(new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero));
            _mockSyncNoteService.Setup(s => s.SyncNewNote(It.IsAny<Note>(), It.IsAny<string>()))
                .Returns(Task.CompletedTask);
            _mockSyncNoteService.Setup(s => s.SyncEditNote(It.IsAny<Note>(), It.IsAny<string>(), It.IsAny<Note>()))
                .Returns(Task.CompletedTask);
            _mockSyncNoteService.Setup(s => s.SyncDeleteNote(It.IsAny<Note>()))
                .Returns(Task.CompletedTask);
            _mockSyncNoteService.Setup(s => s.SyncUndeleteNote(It.IsAny<Note>()))
                .Returns(Task.CompletedTask);

            _noteService = new NoteService(
                _searchService.Object,
                _mockEmbeddingService.Object,
                _semanticSearchOptions,
                syncServices,
                _mockNoteTagService.Object,
                _mockNoteRepository.Object,
                _mockLongNoteRepository.Object,
                _mockMapper.Object,
                _mockLogger.Object,
                _fakeTimeProvider
            );
        }
        [Test]
        public void GetTimestamps_WithRecentStartDate_ReturnsExpectedCount()
        {
            // Arrange - Start date 2 months ago
            var timeZone = "UTC";
            var fixedToday = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
            var startDate = fixedToday.AddMonths(-2);
            var startTimestamp = startDate.ToUnixTimeSeconds();

            // Set time for this test
            _fakeTimeProvider.SetUtcNow(fixedToday);

            // Act
            var result = _noteService._GetTimestamps(startTimestamp, timeZone);

            // Assert
            Assert.That(result.Length, Is.GreaterThanOrEqualTo(2), "Should include at least yesterday and today");
            Assert.That(result.Length, Is.LessThanOrEqualTo(10), "Should not return excessive timestamps");

            // Verify results are sorted (older dates first)
            for (int i = 1; i < result.Length; i++)
            {
                Assert.That(result[i], Is.GreaterThanOrEqualTo(result[i - 1]),
                    "Timestamps should be in ascending order");
            }
        }

        [Test]
        public void GetTimestamps_WithOldStartDate_IncludesYearlyMilestones()
        {
            // Arrange - Start date 3 years ago, same month/day as today
            var timeZone = "UTC";
            var fixedToday = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
            var startDate = new DateTimeOffset(fixedToday.Year - 3, fixedToday.Month, fixedToday.Day,
                12, 0, 0, TimeSpan.Zero);
            var startTimestamp = startDate.ToUnixTimeSeconds();

            // Set time for this test
            _fakeTimeProvider.SetUtcNow(fixedToday);

            // Act
            var result = _noteService._GetTimestamps(startTimestamp, timeZone);

            // Assert
            Assert.That(result.Length, Is.GreaterThanOrEqualTo(5),
                "Should include yearly anniversaries plus recent periods");

            // Should contain timestamps that could be yearly anniversaries
            var yearlyCount = result.Count(ts =>
            {
                var date = DateTimeOffset.FromUnixTimeSeconds(ts);
                return date.Month == fixedToday.Month && date.Day == fixedToday.Day && date.Year < fixedToday.Year;
            });
            Assert.That(yearlyCount, Is.GreaterThanOrEqualTo(1),
                "Should contain at least one yearly anniversary");
        }

        [Test]
        public void GetTimestamps_WithValidTimeZone_DoesNotThrow()
        {
            // Arrange
            var timeZones = new[] { "UTC", "America/New_York", "Europe/London", "Asia/Shanghai" };
            var fixedToday = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
            var startTimestamp = fixedToday.AddMonths(-6).ToUnixTimeSeconds();

            // Set time for this test
            _fakeTimeProvider.SetUtcNow(fixedToday);

            foreach (var timeZone in timeZones)
            {
                // Act & Assert
                Assert.DoesNotThrow(() =>
                {
                    var result = _noteService._GetTimestamps(startTimestamp, timeZone);
                    Assert.That(result, Is.Not.Null);
                    Assert.That(result.Length, Is.GreaterThan(0));
                }, $"Should handle timezone {timeZone} without throwing");
            }
        }

        [Test]
        public void GetTimestamps_WithSameDayStartDate_ReturnsMinimumTimestamps()
        {
            // Arrange - Start date is today
            var timeZone = "UTC";
            var fixedToday = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
            var startTimestamp = fixedToday.ToUnixTimeSeconds();

            // Set time for this test
            _fakeTimeProvider.SetUtcNow(fixedToday);

            // Act
            var result = _noteService._GetTimestamps(startTimestamp, timeZone);

            // Assert
            Assert.That(result.Length, Is.GreaterThanOrEqualTo(1),
                "Should include today at minimum when start date is same day");
        }

        [Test]
        public void GetTimestamps_WithFarFutureStartDate_HandlesGracefully()
        {
            // Arrange - Start date in future (edge case)
            var timeZone = "UTC";
            var fixedToday = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
            var futureStartTimestamp = fixedToday.AddYears(1).ToUnixTimeSeconds();

            // Set time for this test
            _fakeTimeProvider.SetUtcNow(fixedToday);

            // Act
            var result = _noteService._GetTimestamps(futureStartTimestamp, timeZone);

            // Assert - Should return empty array since start date is in future
            Assert.That(result.Length, Is.EqualTo(0),
                "Should return no timestamps when start date is in future");
        }

        [Test]
        public void GetTimestamps_ConsistentResults_ForSameInput()
        {
            // Arrange
            var timeZone = "UTC";
            var startTimestamp = DateTimeOffset.UtcNow.AddMonths(-1).ToUnixTimeSeconds();

            // Act - Call multiple times
            var result1 = _noteService._GetTimestamps(startTimestamp, timeZone);
            var result2 = _noteService._GetTimestamps(startTimestamp, timeZone);

            // Assert - Results should be identical (within same second)
            Assert.That(result1.Length, Is.EqualTo(result2.Length));
            for (int i = 0; i < result1.Length; i++)
            {
                Assert.That(Math.Abs(result1[i] - result2[i]), Is.LessThanOrEqualTo(1),
                    "Results should be consistent within 1 second");
            }
        }

        [Test]
        public void GetTimestamps_AnniversaryDates_RespectHistoricalTimezoneOffsets()
        {
            // Arrange - Use fixed dates to eliminate time-based race conditions
            var timeZone = "America/New_York";
            var fixedToday = new DateTimeOffset(2024, 7, 15, 10, 30, 0, TimeSpan.Zero); // Fixed "today" (after default)
            var startDate = fixedToday.AddYears(-5); // Start date 5 years ago
            var startTimestamp = startDate.ToUnixTimeSeconds();

            // Set time for this test (must be after the default time)
            _fakeTimeProvider.SetUtcNow(fixedToday);

            // Act
            var result = _noteService._GetTimestamps(startTimestamp, timeZone);

            // Assert
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Length, Is.GreaterThan(0));

            // The method should return timestamps for "today's" month/day in previous years
            var currentMonth = fixedToday.Month; // July
            var currentDay = fixedToday.Day; // 15th

            // Find timestamps that match today's month/day from previous years
            var anniversaryTimestamps = result.Where(ts =>
            {
                var nyTimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
                var date = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(ts), nyTimeZone);
                return date.Month == currentMonth && date.Day == currentDay && date.Year < fixedToday.Year;
            }).ToList();

            // Should have at least one anniversary (July 15th in years 2019-2023)
            Assert.That(anniversaryTimestamps.Count, Is.GreaterThanOrEqualTo(1),
                "Should contain anniversary dates for same month/day in previous years");

            // Verify that anniversary dates are correctly set to start of day
            foreach (var ts in anniversaryTimestamps)
            {
                var nyTimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
                var date = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(ts), nyTimeZone);
                Assert.That(date.Month, Is.EqualTo(currentMonth), $"Anniversary should be in month {currentMonth}, got {date:yyyy-MM-dd}");
                Assert.That(date.Day, Is.EqualTo(currentDay), $"Anniversary should be on day {currentDay}, got {date:yyyy-MM-dd}");
                Assert.That(date.Hour, Is.EqualTo(0), "Anniversary should be at start of day");
                Assert.That(date.Minute, Is.EqualTo(0), "Anniversary should be at start of day");
            }

            // Verify we have the expected years (2019, 2020, 2021, 2022, 2023)
            var expectedYears = new[] { 2019, 2020, 2021, 2022, 2023 };
            var actualYears = anniversaryTimestamps.Select(ts =>
            {
                var nyTimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
                var date = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(ts), nyTimeZone);
                return date.Year;
            }).OrderBy(y => y).ToArray();

            Assert.That(actualYears, Is.EqualTo(expectedYears),
                $"Expected anniversary years {string.Join(", ", expectedYears)}, got {string.Join(", ", actualYears)}");
        }

        [Test]
        public void TryCreateDate_WithDifferentYears_UsesCorrectTimezoneOffset()
        {
            // Arrange - Test with a timezone that has DST changes
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

            // Test different scenarios
            var testCases = new[]
            {
                new { Year = 2023, Month = 6, Day = 29, Description = "Summer 2023 (EDT)" },
                new { Year = 2023, Month = 12, Day = 29, Description = "Winter 2023 (EST)" },
                new { Year = 2020, Month = 6, Day = 29, Description = "Summer 2020 (EDT)" }
            };

            foreach (var testCase in testCases)
            {
                // Act
                var success = NoteService._TryCreateDate(testCase.Year, testCase.Month, testCase.Day, timeZone, out var timestamp);

                // Assert
                Assert.That(success, Is.True, $"Should successfully create date for {testCase.Description}");

                var resultDate = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(timestamp), timeZone);
                Assert.That(resultDate.Year, Is.EqualTo(testCase.Year), $"Year mismatch for {testCase.Description}");
                Assert.That(resultDate.Month, Is.EqualTo(testCase.Month), $"Month mismatch for {testCase.Description}");
                Assert.That(resultDate.Day, Is.EqualTo(testCase.Day), $"Day mismatch for {testCase.Description}");
                Assert.That(resultDate.Hour, Is.EqualTo(0), $"Hour should be 0 for {testCase.Description}");
                Assert.That(resultDate.Minute, Is.EqualTo(0), $"Minute should be 0 for {testCase.Description}");
            }
        }

        [Test]
        public void GetTimestamps_MonthEndDates_HandlesCorrectly()
        {
            // Arrange - Test edge case where today is month-end
            var timeZone = "UTC";

            // Test cases for month-end dates that could be problematic
            var testCases = new[]
            {
                new { CurrentDate = new DateTimeOffset(2025, 1, 31, 12, 0, 0, TimeSpan.Zero), Description = "January 31st" },
                new { CurrentDate = new DateTimeOffset(2025, 3, 31, 12, 0, 0, TimeSpan.Zero), Description = "March 31st" },
                new { CurrentDate = new DateTimeOffset(2025, 5, 31, 12, 0, 0, TimeSpan.Zero), Description = "May 31st" },
                new { CurrentDate = new DateTimeOffset(2024, 2, 29, 12, 0, 0, TimeSpan.Zero), Description = "Leap year Feb 29th" }
            };

            foreach (var testCase in testCases)
            {
                // Simulate different "today" dates by creating start dates that would make the logic trigger
                var startDate = testCase.CurrentDate.AddYears(-2); // 2 years ago
                var startTimestamp = startDate.ToUnixTimeSeconds();

                // Act - This would use the current system time, so we can't directly test this
                // But we can test the AddMonths behavior that's problematic
                var result = _noteService._GetTimestamps(startTimestamp, timeZone);

                // Assert - Just verify it doesn't throw and returns reasonable results
                Assert.That(result, Is.Not.Null, $"Should not fail for {testCase.Description}");
                Assert.That(result.Length, Is.GreaterThan(0), $"Should return timestamps for {testCase.Description}");

                // The issue we're looking for is that AddMonths(-1) on March 31 gives Feb 28, not March 1
                // This test documents the current behavior
            }
        }

        [Test]
        public void AddMonths_EdgeCases_DemonstratesProblem()
        {
            // Arrange - Demonstrate the AddMonths issue
            var march31 = new DateTimeOffset(2025, 3, 31, 12, 0, 0, TimeSpan.Zero);
            var jan31 = new DateTimeOffset(2025, 1, 31, 12, 0, 0, TimeSpan.Zero);
            var may31 = new DateTimeOffset(2025, 5, 31, 12, 0, 0, TimeSpan.Zero);

            // Act
            var oneMonthBeforeMarch31 = march31.AddMonths(-1);  // Will be Feb 28, not March 1
            var oneMonthBeforeJan31 = jan31.AddMonths(-1);     // Will be Dec 31, which is correct
            var oneMonthBeforeMay31 = may31.AddMonths(-1);     // Will be Apr 30, not May 1

            // Assert - Document the current behavior
            Assert.That(oneMonthBeforeMarch31.Day, Is.EqualTo(28),
                "March 31 - 1 month = Feb 28 (not March 1) - this might not be what users expect");
            Assert.That(oneMonthBeforeJan31.Day, Is.EqualTo(31),
                "January 31 - 1 month = Dec 31 - this is correct");
            Assert.That(oneMonthBeforeMay31.Day, Is.EqualTo(30),
                "May 31 - 1 month = Apr 30 (not May 1) - this might not be what users expect");
        }

        [Test]
        public void TryGetExactMonthOffset_WithValidDates_ReturnsTrue()
        {
            // Arrange
            var march28 = new DateTimeOffset(2025, 3, 28, 12, 0, 0, TimeSpan.Zero);
            var january31 = new DateTimeOffset(2025, 1, 31, 12, 0, 0, TimeSpan.Zero);

            // Act & Assert - Dates that should work
            Assert.That(NoteService._TryGetExactMonthOffset(march28, -1, out var feb28), Is.True,
                "March 28 should have a valid Feb 28");
            Assert.That(feb28.Month, Is.EqualTo(2));
            Assert.That(feb28.Day, Is.EqualTo(28));

            Assert.That(NoteService._TryGetExactMonthOffset(january31, -1, out var dec31), Is.True,
                "January 31 should have a valid Dec 31");
            Assert.That(dec31.Month, Is.EqualTo(12));
            Assert.That(dec31.Day, Is.EqualTo(31));
        }

        [Test]
        public void TryGetExactMonthOffset_WithInvalidDates_ReturnsFalse()
        {
            // Arrange
            var march29 = new DateTimeOffset(2025, 3, 29, 12, 0, 0, TimeSpan.Zero);
            var march30 = new DateTimeOffset(2025, 3, 30, 12, 0, 0, TimeSpan.Zero);
            var march31 = new DateTimeOffset(2025, 3, 31, 12, 0, 0, TimeSpan.Zero);
            var may31 = new DateTimeOffset(2025, 5, 31, 12, 0, 0, TimeSpan.Zero);

            // Act & Assert - Dates that should NOT work (Feb doesn't have 29, 30, 31 in 2025)
            Assert.That(NoteService._TryGetExactMonthOffset(march29, -1, out _), Is.False,
                "March 29 -> Feb 29 doesn't exist in 2025");
            Assert.That(NoteService._TryGetExactMonthOffset(march30, -1, out _), Is.False,
                "March 30 -> Feb 30 doesn't exist");
            Assert.That(NoteService._TryGetExactMonthOffset(march31, -1, out _), Is.False,
                "March 31 -> Feb 31 doesn't exist");
            Assert.That(NoteService._TryGetExactMonthOffset(may31, -1, out _), Is.False,
                "May 31 -> Apr 31 doesn't exist");
        }

        [Test]
        public void TryGetExactMonthOffset_WithLeapYear_HandlesFebruary29()
        {
            // Arrange - 2024 is a leap year
            var march29_2024 = new DateTimeOffset(2024, 3, 29, 12, 0, 0, TimeSpan.Zero);
            var march29_2025 = new DateTimeOffset(2025, 3, 29, 12, 0, 0, TimeSpan.Zero);

            // Act & Assert
            Assert.That(NoteService._TryGetExactMonthOffset(march29_2024, -1, out var feb29_2024), Is.True,
                "March 29 2024 -> Feb 29 2024 should work (leap year)");
            Assert.That(feb29_2024.Day, Is.EqualTo(29));
            Assert.That(feb29_2024.Month, Is.EqualTo(2));
            Assert.That(feb29_2024.Year, Is.EqualTo(2024));

            Assert.That(NoteService._TryGetExactMonthOffset(march29_2025, -1, out _), Is.False,
                "March 29 2025 -> Feb 29 2025 should NOT work (not leap year)");
        }

        [Test]
        public void GetTimestamps_SkipsNonExistentMonthlyDates()
        {
            // Fake time is 2024-06-15 12:00 UTC; use a fixed start 2 years prior so it
            // stays within the fake time window regardless of when tests run.
            var timeZone = "UTC";
            var startTimestamp = new DateTimeOffset(2022, 6, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

            // Act
            var result = _noteService._GetTimestamps(startTimestamp, timeZone);

            // Assert
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Length, Is.GreaterThan(0));

            // The improvement means some monthly milestones might be skipped,
            // but we should still get at least yesterday and today
            Assert.That(result.Length, Is.GreaterThanOrEqualTo(2),
                "Should always include at least yesterday and today");
        }

        [Test]
        public void GetTimestamps_RespectsStartDateBoundary()
        {
            // Test that monthly milestones don't go before the start date
            var timeZone = "UTC";
            var fixedToday = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
            var recentStartDate = fixedToday.AddMonths(-2); // Only 2 months of history
            var startTimestamp = recentStartDate.ToUnixTimeSeconds();

            // Set time for this test
            _fakeTimeProvider.SetUtcNow(fixedToday);

            // Act
            var result = _noteService._GetTimestamps(startTimestamp, timeZone);

            // Assert
            Assert.That(result, Is.Not.Null);

            // Should not include 6-month or 3-month milestones since they'd be before start date
            // but should include 1-month milestone (if the date exists in target month)
            var allTimestamps = result.Select(ts => DateTimeOffset.FromUnixTimeSeconds(ts)).ToList();
            var oldestTimestamp = allTimestamps.Min();

            Assert.That(oldestTimestamp, Is.GreaterThanOrEqualTo(recentStartDate),
                "No timestamp should be earlier than the start date");
        }

        // Issue #8: periodic milestone day-start off-by-one in southern-hemisphere DST zones.
        // today = 2025-08-25 23:00 Australia/Sydney → unpatched resolves -6-month milestone to
        // 2025-02-26; the correct calendar day is 2025-02-25. The +1 only reproduces in the
        // southern hemisphere; fixed-offset zones (Asia/Shanghai) have no bug and act as a
        // no-op guard against a false RED.
        [TestCase("Australia/Sydney", 2025, 2, 25, TestName = "GetTimestamps_SouthernDst_SixMonthMilestone_ReturnsCorrectDay")]
        [TestCase("Australia/Sydney", 2025, 5, 25, TestName = "GetTimestamps_SouthernDst_ThreeMonthMilestone_ReturnsCorrectDay")]
        [TestCase("Australia/Sydney", 2025, 7, 25, TestName = "GetTimestamps_SouthernDst_OneMonthMilestone_ReturnsCorrectDay")]
        [TestCase("Asia/Shanghai", 2025, 2, 25, TestName = "GetTimestamps_FixedOffset_SixMonthMilestone_NoOpGuard")]
        [TestCase("Asia/Shanghai", 2025, 5, 25, TestName = "GetTimestamps_FixedOffset_ThreeMonthMilestone_NoOpGuard")]
        [TestCase("Asia/Shanghai", 2025, 7, 25, TestName = "GetTimestamps_FixedOffset_OneMonthMilestone_NoOpGuard")]
        public void GetTimestamps_PeriodicMilestone_ReturnsCalendarDayStart(
            string timeZoneId, int expectedYear, int expectedMonth, int expectedDay)
        {
            // Arrange - today = 2025-08-25 23:00 in the target timezone.
            var targetTimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            var todayLocal = new DateTimeOffset(2025, 8, 25, 23, 0, 0, targetTimeZone.GetUtcOffset(
                new DateTime(2025, 8, 25, 0, 0, 0, DateTimeKind.Unspecified)));
            _fakeTimeProvider.SetUtcNow(todayLocal);

            // startDate well before the -6-month milestone so it isn't filtered out.
            var startTimestamp = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

            // Act
            var result = _noteService._GetTimestamps(startTimestamp, timeZoneId);

            // Assert - find the periodic-milestone timestamp for the expected calendar day.
            var expectedDayStart = new DateTimeOffset(expectedYear, expectedMonth, expectedDay, 0, 0, 0,
                targetTimeZone.GetUtcOffset(new DateTime(expectedYear, expectedMonth, expectedDay, 0, 0, 0, DateTimeKind.Unspecified)))
                .ToUnixTimeSeconds();

            Assert.That(result, Does.Contain(expectedDayStart),
                $"Expected -milestone day-start {expectedYear:0000}-{expectedMonth:00}-{expectedDay:00} in {timeZoneId} " +
                $"but got: {string.Join(", ", result.Select(ts => DateTimeOffset.FromUnixTimeSeconds(ts).ToString("yyyy-MM-dd HH:mm:ss zzz")))}");
        }

        // Issue #17: the periodic-milestone path must honor the per-date zone offset even when
        // today and the target date fall in different DST seasons.
        // Case A (southern hemisphere): today = 2025-10-08 +11 (AEDT, post-DST-start),
        //   -6 month = 2025-04-08 +10 (AEST, post-DST-end).  Buggy path returns +11 midnight.
        // Case B (northern hemisphere): today = 2025-04-01 -04 (EDT, post-spring-forward),
        //   -1 month = 2025-03-01 -05 (EST, pre-spring-forward).  Buggy path returns -04 midnight.
        [TestCase("Australia/Sydney", 2025, 10, 8, 12, 2025, 4, 8,
            TestName = "GetTimestamps_PeriodicMilestone_SydneyCrossDstSeason_ReturnsCorrectDayStart")]
        [TestCase("America/New_York", 2025, 4, 1, 12, 2025, 3, 1,
            TestName = "GetTimestamps_PeriodicMilestone_NewYorkCrossDstSeason_ReturnsCorrectDayStart")]
        public void GetTimestamps_PeriodicMilestone_CrossDstSeason_ReturnsCalendarDayStart(
            string timeZoneId,
            int todayYear, int todayMonth, int todayDay, int todayHour,
            int expectedYear, int expectedMonth, int expectedDay)
        {
            var targetTz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            var todayOffset = targetTz.GetUtcOffset(new DateTime(todayYear, todayMonth, todayDay, 0, 0, 0, DateTimeKind.Unspecified));
            var todayLocal = new DateTimeOffset(todayYear, todayMonth, todayDay, todayHour, 0, 0, todayOffset);
            _fakeTimeProvider.SetUtcNow(todayLocal);

            var startTimestamp = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

            var result = _noteService._GetTimestamps(startTimestamp, timeZoneId);

            var expectedDate = new DateTime(expectedYear, expectedMonth, expectedDay, 0, 0, 0, DateTimeKind.Unspecified);
            var correctOffset = targetTz.GetUtcOffset(expectedDate);
            var correctDayStart = new DateTimeOffset(expectedDate, correctOffset).ToUnixTimeSeconds();
            var wrongDayStart = new DateTimeOffset(expectedDate, todayOffset).ToUnixTimeSeconds();

            Assert.That(result, Does.Contain(correctDayStart),
                $"Expected {expectedYear:0000}-{expectedMonth:00}-{expectedDay:00} 00:00 {correctOffset} " +
                $"but got: {string.Join(", ", result.Select(ts => DateTimeOffset.FromUnixTimeSeconds(ts).ToString("yyyy-MM-dd HH:mm:ss zzz")))}");

            if (wrongDayStart != correctDayStart)
            {
                Assert.That(result, Does.Not.Contain(wrongDayStart),
                    $"Bug regressed: result contains wrong-offset midnight {expectedYear:0000}-{expectedMonth:00}-{expectedDay:00} 00:00 {todayOffset}");
            }

            CollectionAssert.AllItemsAreUnique(result, "Timestamps must be unique — duplicate implies a wrong-offset twin appeared alongside the correct one");
        }

        // Yesterday/today entries must also use the per-date zone offset
        // (issue #8 follow-up). When `today` is the first day after a DST
        // transition, `yesterday` straddles the boundary and the source
        // offset is wrong; on the transition day itself `today`'s midnight
        // is in the prior season. This test pins the spring-forward case
        // (NYC 2025-03-10): yesterday must resolve to 2025-03-09, today
        // to 2025-03-10. Unpatched yesterday → 2025-03-08 (RED).
        [TestCase("America/New_York", 2025, 3, 9, 2025, 3, 10, TestName = "GetTimestamps_DstTransition_NycSpringForward_IncludesYesterdayAndToday")]
        [TestCase("Europe/Berlin", 2025, 10, 25, 2025, 10, 26, TestName = "GetTimestamps_DstTransition_BerlinFallBack_IncludesYesterdayAndToday")]
        [TestCase("Australia/Sydney", 2025, 10, 4, 2025, 10, 5, TestName = "GetTimestamps_DstTransition_SydneySpringForward_IncludesYesterdayAndToday")]
        public void GetTimestamps_DstTransition_IncludesYesterdayAndToday(
            string timeZoneId, int yYear, int yMonth, int yDay, int tYear, int tMonth, int tDay)
        {
            // Arrange - drive `today` to a time after the local transition so
            // the source offset is the post-transition one.
            var targetTimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            var todayLocal = new DateTimeOffset(tYear, tMonth, tDay, 12, 0, 0,
                targetTimeZone.GetUtcOffset(new DateTime(tYear, tMonth, tDay, 0, 0, 0, DateTimeKind.Unspecified)));
            _fakeTimeProvider.SetUtcNow(todayLocal);

            var startTimestamp = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

            // Act
            var result = _noteService._GetTimestamps(startTimestamp, timeZoneId);

            // Assert
            var yDayStart = new DateTimeOffset(yYear, yMonth, yDay, 0, 0, 0,
                targetTimeZone.GetUtcOffset(new DateTime(yYear, yMonth, yDay, 0, 0, 0, DateTimeKind.Unspecified)))
                .ToUnixTimeSeconds();
            var tDayStart = new DateTimeOffset(tYear, tMonth, tDay, 0, 0, 0,
                targetTimeZone.GetUtcOffset(new DateTime(tYear, tMonth, tDay, 0, 0, 0, DateTimeKind.Unspecified)))
                .ToUnixTimeSeconds();

            Assert.That(result, Does.Contain(yDayStart),
                $"Expected yesterday day-start {yYear:0000}-{yMonth:00}-{yDay:00} in {timeZoneId} " +
                $"but got: {string.Join(", ", result.Select(ts => DateTimeOffset.FromUnixTimeSeconds(ts).ToString("yyyy-MM-dd HH:mm:ss zzz")))}");
            Assert.That(result, Does.Contain(tDayStart),
                $"Expected today day-start {tYear:0000}-{tMonth:00}-{tDay:00} in {timeZoneId} " +
                $"but got: {string.Join(", ", result.Select(ts => DateTimeOffset.FromUnixTimeSeconds(ts).ToString("yyyy-MM-dd HH:mm:ss zzz")))}");
        }

    }

}
