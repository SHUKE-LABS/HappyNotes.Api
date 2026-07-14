using System.Linq.Expressions;
using Api.Framework;
using HappyNotes.Entities;
using Moq;
using SqlSugar;

namespace HappyNotes.Services.Tests;

[TestFixture]
public class NoteTagServiceTests
{
    private Mock<IRepositoryBase<NoteTag>> _mockRepo;
    private NoteTagService _service;

    [SetUp]
    public void Setup()
    {
        _mockRepo = new Mock<IRepositoryBase<NoteTag>>();
        _service = new NoteTagService(_mockRepo.Object);
    }

    [Test]
    public async Task Upsert_TagDoesNotExist_InsertsNewTag()
    {
        var note = new Note { Id = 1, UserId = 10 };
        NoteTag? insertedTag = null;
        _mockRepo.Setup(r => r.GetFirstOrDefaultAsync(It.IsAny<Expression<Func<NoteTag, bool>>>(), null))
            .ReturnsAsync((NoteTag?)null);
        _mockRepo.Setup(r => r.InsertAsync(It.IsAny<NoteTag>()))
            .Callback<NoteTag>(t => insertedTag = t)
            .ReturnsAsync(true);

        await _service.Upsert(note, new List<string> { "Foo" });

        _mockRepo.Verify(r => r.InsertAsync(It.IsAny<NoteTag>()), Times.Once);
        Assert.That(insertedTag, Is.Not.Null);
        Assert.That(insertedTag!.NoteId, Is.EqualTo(note.Id));
        Assert.That(insertedTag.UserId, Is.EqualTo(note.UserId));
        Assert.That(insertedTag.Tag, Is.EqualTo("foo"));
    }

    [Test]
    public async Task Upsert_TagAlreadyExists_DoesNotInsert()
    {
        var note = new Note { Id = 1, UserId = 10 };
        _mockRepo.Setup(r => r.GetFirstOrDefaultAsync(It.IsAny<Expression<Func<NoteTag, bool>>>(), null))
            .ReturnsAsync(new NoteTag { NoteId = note.Id, UserId = note.UserId, Tag = "foo" });

        await _service.Upsert(note, new List<string> { "Foo" });

        _mockRepo.Verify(r => r.InsertAsync(It.IsAny<NoteTag>()), Times.Never);
    }

    [Test]
    public async Task Delete_NormalizesTagCaseAndScopesToNoteId()
    {
        Expression<Func<NoteTag, bool>>? capturedPredicate = null;
        _mockRepo.Setup(r => r.DeleteAsync(It.IsAny<Expression<Func<NoteTag, bool>>>()))
            .Callback<Expression<Func<NoteTag, bool>>>(expr => capturedPredicate = expr)
            .ReturnsAsync(true);

        await _service.Delete(noteId: 5, new List<string> { "Foo" });

        _mockRepo.Verify(r => r.DeleteAsync(It.IsAny<Expression<Func<NoteTag, bool>>>()), Times.Once);
        Assert.That(capturedPredicate, Is.Not.Null);
        var matches = capturedPredicate!.Compile();
        Assert.That(matches(new NoteTag { NoteId = 5, Tag = "foo" }), Is.True);
        Assert.That(matches(new NoteTag { NoteId = 5, Tag = "bar" }), Is.False);
        Assert.That(matches(new NoteTag { NoteId = 6, Tag = "foo" }), Is.False);
    }

    [Test]
    public async Task RemoveUnusedTags_KeepsListedTagsAndScopesToNoteId()
    {
        Expression<Func<NoteTag, bool>>? capturedPredicate = null;
        _mockRepo.Setup(r => r.DeleteAsync(It.IsAny<Expression<Func<NoteTag, bool>>>()))
            .Callback<Expression<Func<NoteTag, bool>>>(expr => capturedPredicate = expr)
            .ReturnsAsync(true);

        await _service.RemoveUnusedTags(noteId: 5, new List<string> { "foo", "bar" });

        Assert.That(capturedPredicate, Is.Not.Null);
        var matches = capturedPredicate!.Compile();
        Assert.That(matches(new NoteTag { NoteId = 5, Tag = "foo" }), Is.False, "kept tag must not be targeted");
        Assert.That(matches(new NoteTag { NoteId = 5, Tag = "baz" }), Is.True, "unlisted tag must be targeted");
        Assert.That(matches(new NoteTag { NoteId = 6, Tag = "baz" }), Is.False, "must scope to the given note");
    }
}

[TestFixture]
public class NoteTagServiceGetTagDataTests
{
    private SqlSugarClient _db;
    private NoteTagService _service;

    [SetUp]
    public void Setup()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
        });
        // SqlSugar's CodeFirst maps `long` PKs to a SQLite column type that isn't
        // the literal "INTEGER" SQLite requires for AUTOINCREMENT, so the schema
        // is created by hand instead of via db.CodeFirst.InitTables<T>().
        _db.Ado.ExecuteCommand("""
            CREATE TABLE NoteTag (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL,
                NoteId INTEGER NOT NULL,
                Tag TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL
            );
            """);
        _db.Ado.ExecuteCommand("""
            CREATE TABLE Note (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL,
                Content TEXT NOT NULL,
                IsLong INTEGER NOT NULL,
                IsMarkdown INTEGER NOT NULL,
                IsPrivate INTEGER NOT NULL,
                Tags TEXT NOT NULL,
                TelegramMessageIds TEXT,
                MastodonTootIds TEXT,
                FanfouStatusIds TEXT,
                CreatedAt INTEGER NOT NULL,
                UpdatedAt INTEGER,
                DeletedAt INTEGER
            );
            """);

        var mockRepo = new Mock<IRepositoryBase<NoteTag>>();
        mockRepo.Setup(r => r.db).Returns(_db);
        _service = new NoteTagService(mockRepo.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Ado.Connection.Dispose();
    }

    [Test]
    public async Task GetTagData_ExcludesSoftDeletedNotesAndOtherUsers_GroupsAndCountsByTag()
    {
        var keptNoteId = await _db.Insertable(new Note { UserId = 10, Content = "kept" }).ExecuteReturnIdentityAsync();
        var deletedNoteId = await _db.Insertable(new Note { UserId = 10, Content = "deleted" })
            .ExecuteReturnIdentityAsync();
        // EntityBase.DeletedAt is [SugarColumn(IsOnlyIgnoreInsert = true)] — it is
        // silently dropped on insert, so soft-delete via an update, same as production.
        await _db.Updateable<Note>().SetColumns(n => new Note { DeletedAt = 1000 })
            .Where(n => n.Id == deletedNoteId).ExecuteCommandAsync();

        await _db.Insertable(new List<NoteTag>
        {
            new() { NoteId = keptNoteId, UserId = 10, Tag = "foo", CreatedAt = 1 },
            new() { NoteId = keptNoteId, UserId = 10, Tag = "foo", CreatedAt = 2 },
            new() { NoteId = deletedNoteId, UserId = 10, Tag = "bar", CreatedAt = 3 },
            new() { NoteId = keptNoteId, UserId = 99, Tag = "baz", CreatedAt = 4 },
        }).ExecuteCommandAsync();

        var result = await _service.GetTagData(10);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Tag, Is.EqualTo("foo"));
        Assert.That(result[0].Count, Is.EqualTo(2));
    }

    [Test]
    public async Task GetTagData_RespectsLimitAndOrdersByCountDescending()
    {
        var noteId = await _db.Insertable(new Note { UserId = 10, Content = "a" }).ExecuteReturnIdentityAsync();

        await _db.Insertable(new List<NoteTag>
        {
            new() { NoteId = noteId, UserId = 10, Tag = "popular", CreatedAt = 1 },
            new() { NoteId = noteId, UserId = 10, Tag = "popular", CreatedAt = 2 },
            new() { NoteId = noteId, UserId = 10, Tag = "rare", CreatedAt = 3 },
        }).ExecuteCommandAsync();

        var result = await _service.GetTagData(10, limit: 1);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Tag, Is.EqualTo("popular"));
    }
}
