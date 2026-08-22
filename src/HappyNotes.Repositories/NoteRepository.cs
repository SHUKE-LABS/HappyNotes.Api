using System.Linq.Expressions;
using Api.Framework;
using Api.Framework.Models;
using HappyNotes.Entities;
using HappyNotes.Repositories.interfaces;
using SqlSugar;

namespace HappyNotes.Repositories;

public class NoteRepository(ISqlSugarClient dbClient) : RepositoryBase<Note>(dbClient), INoteRepository
{
    public async Task<Note> Get(long noteId)
    {
        return await db.Queryable<Note, LongNote, User>((n, l, u) => new JoinQueryInfos(
                JoinType.Left, n.Id == l.Id,
                JoinType.Inner, n.UserId == u.Id
            ))
            .Where((n, l, u) => n.Id == noteId)
            .Select((n, l, u) => new Note
            {
                Content = n.IsLong ? l.Content : n.Content,
                User = u
            }, true)
            .FirstAsync();
    }

    public async Task<PageData<Note>> GetUserTagNotes(long userId, string tag, int pageSize, int pageNumber,
        bool isAsc = false)
    {
        return await _GetPageDataByTagAsync(pageSize, pageNumber,
            (n, t) =>
                t.Tag.Equals(tag.ToLower()) &&
                n.DeletedAt == null && (n.UserId == userId || n.IsPrivate == false),
            n => n.CreatedAt, isAsc);
    }

    public async Task<PageData<Note>> GetUserNotes(long userId, int pageSize, int pageNumber,
        bool includePrivate = true, bool isAsc = false)
    {
        if (includePrivate)
        {
            return await _GetPageDataAsync(pageSize, pageNumber,
                n => n.UserId == userId && n.DeletedAt == null,
                n => n.CreatedAt, isAsc);

        }

        return await _GetPageDataAsync(pageSize, pageNumber,
            n => n.UserId == userId && n.DeletedAt == null && n.IsPrivate == false,
            n => n.CreatedAt, isAsc);
    }

    public async Task<PageData<Note>> GetPublicNotes(int pageSize, int pageNumber, bool isAsc = false, long? excludeUserId = null)
    {
        if (excludeUserId.HasValue)
        {
            return await _GetPageDataAsync(pageSize, pageNumber,
                n => n.DeletedAt == null && n.IsPrivate == false && n.UserId != excludeUserId.Value,
                n => n.CreatedAt, isAsc);
        }

        return await _GetPageDataAsync(pageSize, pageNumber,
            n => n.DeletedAt == null && n.IsPrivate == false,
            n => n.CreatedAt, isAsc);
    }

    public async Task<PageData<Note>> GetLinkedNotes(long userId, long noteId, int max = 200)
    {
        return await _GetPageDataByTagAsync(max, 1,
            (n, t) =>
                t.Tag.Equals($"@{noteId}") &&
                n.DeletedAt == null &&
                (t.UserId == userId || n.IsPrivate == false),
            n => n.CreatedAt);
    }

    private async Task<PageData<Note>> _GetPageDataAsync(int pageSize = 20, int pageNumber = 1,
        Expression<Func<Note, bool>>? where = null, Expression<Func<Note, object>>? orderBy = null,
        bool isAsc = true)
    {
        var pageData = new PageData<Note>
        {
            PageIndex = pageNumber,
            PageSize = pageSize,
        };
        RefAsync<int> totalCount = 0;
        var result = await db.Queryable<Note>().WhereIF(null != where, where)
            .OrderByIF(orderBy != null, orderBy, isAsc ? OrderByType.Asc : OrderByType.Desc)
            .Mapper(n => n.User, n => n.UserId)
            .ToPageListAsync(pageNumber, pageSize, totalCount);
        pageData.TotalCount = totalCount;
        pageData.DataList = result;
        return pageData;
    }

    private async Task<PageData<Note>> _GetPageDataByTagAsync(int pageSize = 20, int pageNumber = 1,
        Expression<Func<Note, NoteTag, bool>>? where = null,
        Expression<Func<Note, object>>? orderBy = null,
        bool isAsc = true)
    {
        var pageData = new PageData<Note>
        {
            PageIndex = pageNumber,
            PageSize = pageSize,
        };
        RefAsync<int> totalCount = 0;
        var queryable = db.Queryable<Note, NoteTag>((n, t) => new JoinQueryInfos(
                JoinType.Inner, n.Id == t.NoteId
            )).WhereIF(null != where, where)
            .OrderByIF(orderBy != null, orderBy, isAsc ? OrderByType.Asc : OrderByType.Desc);

        List<Note> result = await queryable
            .Mapper(n => n.User, n => n.UserId)
            .ToPageListAsync(pageNumber, pageSize, totalCount);
        pageData.TotalCount = totalCount;
        pageData.DataList = result;
        return pageData;
    }

    public async Task<PageData<Note>> GetUserDeletedNotes(long userId, int pageSize, int pageNumber)
    {
        // Use the existing helper, but filter for deleted notes and order by DeletedAt descending
#pragma warning disable CS8603 // Possible null reference return.
        return await _GetPageDataAsync(pageSize, pageNumber,
            n => n.UserId == userId && n.DeletedAt != null,
            n => n.DeletedAt, // Order by deletion date; safe due to prior null check
            false); // Descending order (most recently deleted first)
#pragma warning restore CS8603 // Possible null reference return.
    }

    public async Task PurgeUserDeletedNotes(long userId)
    {
        // Find IDs of deleted notes for the user
        var deletedNoteIds = await db.Queryable<Note>()
            .Where(n => n.UserId == userId && n.DeletedAt != null)
            .Select(n => n.Id)
            .ToListAsync();

        if (deletedNoteIds.Any())
        {
            // Delete associated LongNotes first (if any)
            await db.Deleteable<LongNote>().Where(ln => deletedNoteIds.Contains(ln.Id)).ExecuteCommandAsync();

            // Delete associated NoteTags (optional, depending on desired behavior - let's include it for cleanup)
            await db.Deleteable<NoteTag>().Where(nt => deletedNoteIds.Contains(nt.NoteId)).ExecuteCommandAsync();

            // Finally, delete the notes themselves
            await db.Deleteable<Note>().Where(n => deletedNoteIds.Contains(n.Id)).ExecuteCommandAsync();
        }
    }

    public async Task UndeleteAsync(long noteId)
    {
        // Use UpdateSetColumns to only update specific fields
        await db.Updateable<Note>()
            .SetColumns(it => new Note { DeletedAt = null }) // Set DeletedAt to null
            .Where(it => it.Id == noteId && it.DeletedAt != null) // Only undelete if it's currently deleted
            .ExecuteCommandAsync();
    }
}
