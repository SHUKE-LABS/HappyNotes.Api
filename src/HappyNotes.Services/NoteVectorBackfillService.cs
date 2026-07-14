using Api.Framework;
using HappyNotes.Entities;
using HappyNotes.Repositories.interfaces;
using HappyNotes.Services.interfaces;
using Microsoft.Extensions.Logging;

namespace HappyNotes.Services;

/// <summary>
/// One-off, resumable, batched job that embeds existing notes and stores their vectors in the Manticore
/// index. Intended to be triggered off-peak (see VectorBackfillAdminController). It walks notes in
/// id-ascending order and persists the last successfully-embedded id to a cursor file so a restart
/// resumes rather than re-embedding from scratch.
/// </summary>
public class NoteVectorBackfillService : INoteVectorBackfillService
{
    private readonly INoteRepository _noteRepository;
    private readonly IRepositoryBase<LongNote> _longNoteRepository;
    private readonly ISearchService _searchService;
    private readonly IEmbeddingService _embeddingService;
    private readonly SemanticSearchOptions _options;
    private readonly ILogger<NoteVectorBackfillService> _logger;

    public NoteVectorBackfillService(
        INoteRepository noteRepository,
        IRepositoryBase<LongNote> longNoteRepository,
        ISearchService searchService,
        IEmbeddingService embeddingService,
        SemanticSearchOptions options,
        ILogger<NoteVectorBackfillService> logger)
    {
        _noteRepository = noteRepository;
        _longNoteRepository = longNoteRepository;
        _searchService = searchService;
        _embeddingService = embeddingService;
        _options = options;
        _logger = logger;
    }

    public async Task<VectorBackfillResult> RunAsync(int? batchSize = null, CancellationToken cancellationToken = default)
    {
        var result = new VectorBackfillResult();

        if (!_options.Enabled)
        {
            result.Completed = false;
            result.Message = "SemanticSearch is disabled; nothing to backfill.";
            return result;
        }

        var size = batchSize ?? _options.BackfillBatchSize;
        if (size <= 0) size = 100;

        var cursor = _ReadCursor();
        _logger.LogInformation("Vector backfill starting from note id > {Cursor}, batch size {BatchSize}", cursor, size);

        while (!cancellationToken.IsCancellationRequested)
        {
            var notes = await _noteRepository.GetTopListAsync(size, w => w.Id > cursor, new List<string> { "Id ASC" });
            if (notes.Count == 0)
            {
                result.Completed = true;
                break;
            }

            foreach (var note in notes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fullContent = await _ReconstructFullContent(note);
                if (string.IsNullOrWhiteSpace(fullContent))
                {
                    cursor = note.Id;
                    result.Processed++;
                    result.Skipped++;
                    continue;
                }

                var embedding = await _embeddingService.EmbedAsync(fullContent, cancellationToken);
                if (embedding == null)
                {
                    // Backend unavailable (or dimension mismatch): stop and keep the cursor at the last
                    // success so the operator can fix the backend and resume without losing progress.
                    _WriteCursor(cursor);
                    result.LastProcessedId = cursor;
                    result.Completed = false;
                    result.Message = $"Stopped at note {note.Id}: embedding backend unavailable. Rerun to resume.";
                    _logger.LogWarning("Vector backfill stopped at note {NoteId}: embedding backend unavailable", note.Id);
                    return result;
                }

                await _searchService.SyncNoteToIndexAsync(note, fullContent, embedding);
                cursor = note.Id;
                result.Processed++;
                result.Embedded++;
            }

            _WriteCursor(cursor);
            result.LastProcessedId = cursor;
            _logger.LogInformation("Vector backfill progress: {Processed} processed ({Embedded} embedded, {Skipped} skipped), cursor at {Cursor}",
                result.Processed, result.Embedded, result.Skipped, cursor);

            if (notes.Count < size)
            {
                result.Completed = true;
                break;
            }
        }

        result.LastProcessedId = cursor;
        result.Message ??= result.Completed
            ? $"Backfill complete: {result.Embedded} embedded, {result.Skipped} skipped."
            : "Backfill cancelled.";
        return result;
    }

    private async Task<string> _ReconstructFullContent(Note note)
    {
        if (note.IsLong)
        {
            var longNote = await _longNoteRepository.GetFirstOrDefaultAsync(x => x.Id == note.Id);
            return longNote?.Content ?? note.Content ?? string.Empty;
        }
        return note.Content ?? string.Empty;
    }

    private long _ReadCursor()
    {
        try
        {
            if (File.Exists(_options.BackfillCursorPath))
            {
                var text = File.ReadAllText(_options.BackfillCursorPath).Trim();
                if (long.TryParse(text, out var value))
                {
                    return value;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read backfill cursor at {Path}; starting from 0", _options.BackfillCursorPath);
        }
        return 0;
    }

    private void _WriteCursor(long cursor)
    {
        try
        {
            File.WriteAllText(_options.BackfillCursorPath, cursor.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist backfill cursor {Cursor} to {Path}", cursor, _options.BackfillCursorPath);
        }
    }
}
