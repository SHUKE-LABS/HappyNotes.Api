namespace HappyNotes.Services.interfaces;

public interface IEmbeddingService
{
    /// <summary>
    /// Generates an embedding vector for the given text. Returns null when semantic search is disabled,
    /// the text is empty, or the embedding backend is unavailable — callers must treat null as
    /// "no vector available" and fall back to keyword-only behavior.
    /// </summary>
    Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default);
}
