using HappyNotes.Common.Enums;
using HappyNotes.Entities;

namespace HappyNotes.Services.interfaces;

public interface ISearchService
{
    Task<(List<long> noteIds, int total)> GetNoteIdsByKeywordAsync(long userId, string query, int pageNumber, int pageSize, NoteFilterType filter = NoteFilterType.Normal);
    Task SyncNoteToIndexAsync(Note note, string fullContent);
    Task DeleteNoteFromIndexAsync(long id);
    Task UndeleteNoteFromIndexAsync(long id);
    Task PurgeUserDeletedNotesFromIndexAsync(long userId);
}
