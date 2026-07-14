namespace HappyNotes.Services.interfaces;

public interface INoteVectorBackfillService
{
    /// <summary>
    /// Embeds existing notes in id-ascending batches and writes their vectors to the index.
    /// Resumable: progress is persisted to a cursor file, so a stopped/failed run continues where it left off.
    /// </summary>
    Task<VectorBackfillResult> RunAsync(int? batchSize = null, CancellationToken cancellationToken = default);
}

public class VectorBackfillResult
{
    public int Processed { get; set; }
    public int Embedded { get; set; }
    public int Skipped { get; set; }
    public long LastProcessedId { get; set; }

    /// <summary>True when the whole corpus was processed; false when the run stopped early (e.g. backend down) and can be resumed.</summary>
    public bool Completed { get; set; }

    public string? Message { get; set; }
}
