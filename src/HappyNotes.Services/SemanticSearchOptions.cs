namespace HappyNotes.Services;

/// <summary>
/// Configuration for semantic (vector) note search. When <see cref="Enabled"/> is false the
/// search path behaves exactly like the legacy keyword-only search.
/// </summary>
public class SemanticSearchOptions
{
    /// <summary>Master switch. When false, no embeddings are generated and search stays keyword-only.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Self-hosted embedding endpoint (default: Ollama). Expected request/response shape:
    /// POST { "model": "&lt;model&gt;", "prompt": "&lt;text&gt;" } -> { "embedding": [floats] }.
    /// </summary>
    public string EmbeddingEndpoint { get; set; } = string.Empty;

    /// <summary>Embedding model name passed to the endpoint (e.g. bge-m3).</summary>
    public string EmbeddingModel { get; set; } = "bge-m3";

    /// <summary>Vector dimension. Must match both the model output and the noteindex float_vector knn_dims.</summary>
    public int Dimensions { get; set; } = 1024;

    /// <summary>HNSW similarity metric documented in create_table.sql (cosine for bge-m3).</summary>
    public string Similarity { get; set; } = "cosine";

    /// <summary>Number of nearest semantic candidates to retrieve per query.</summary>
    public int TopK { get; set; } = 50;

    /// <summary>
    /// Optional distance ceiling on the KNN result. Manticore returns _knn_dist (lower = closer);
    /// candidates with _knn_dist greater than this are dropped. 0 disables the ceiling.
    /// </summary>
    public double MaxDistance { get; set; } = 0;

    /// <summary>
    /// Upper bound on keyword hits fetched purely to dedup semantic candidates against keyword hits.
    /// Keyword paging itself is NOT capped by this — deep pages are fetched directly from Manticore.
    /// </summary>
    public int KeywordMergeCap { get; set; } = 1000;

    /// <summary>Per-call embedding HTTP timeout. Bounds how long a slow backend can block the sync queue.</summary>
    public int EmbeddingTimeoutSeconds { get; set; } = 30;

    /// <summary>Batch size for the resumable backfill job.</summary>
    public int BackfillBatchSize { get; set; } = 100;

    /// <summary>File path where the backfill job persists its resume cursor (last embedded note id).</summary>
    public string BackfillCursorPath { get; set; } = "vector-backfill.cursor";
}
