using HappyNotes.Common.Enums;
using HappyNotes.Entities;

namespace HappyNotes.Services.interfaces;

public interface ISearchService
{
    Task<(List<long> noteIds, int total)> GetNoteIdsByKeywordAsync(long userId, string query, int pageNumber, int pageSize, NoteFilterType filter = NoteFilterType.Normal);

    /// <summary>
    /// Returns note ids ranked by vector similarity (nearest first), filtered by owner and delete-state
    /// identically to the keyword path. Candidates with distance above <paramref name="maxDistance"/>
    /// (when &gt; 0) are dropped.
    /// </summary>
    Task<List<long>> GetSemanticNoteIdsAsync(long userId, float[] queryVector, NoteFilterType filter, int k, double maxDistance = 0);

    /// <summary>
    /// Upserts the note into the index via REPLACE. When <paramref name="embedding"/> is non-null the
    /// vector is written in the same REPLACE (Manticore REPLACE is the only way to set a float_vector,
    /// so a single writer per doc avoids clobbering the vector).
    /// </summary>
    Task SyncNoteToIndexAsync(Note note, string fullContent, float[]? embedding = null);
    Task DeleteNoteFromIndexAsync(long id);
    Task UndeleteNoteFromIndexAsync(long id);
    Task PurgeUserDeletedNotesFromIndexAsync(long userId);
}
