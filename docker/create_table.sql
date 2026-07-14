DROP TABLE IF EXISTS NoteIndex;

-- The Embedding float_vector adds semantic (KNN) search to the same index used for keyword search.
-- knn_dims must equal SemanticSearch:Dimensions (bge-m3 = 1024); hnsw_similarity must equal
-- SemanticSearch:Similarity (cosine). float_vector + KNN require a real-time table (Manticore >= 6.2).
-- Adding/removing this column requires recreating the table and a full reindex (see docker/manticore-init
-- and the resumable vector backfill job / api/admin/vector-backfill endpoint).
CREATE TABLE NoteIndex (
    Id BIGINT,
    UserId BIGINT,
    IsLong INT,
    IsPrivate INT,
    IsMarkdown INT,
    CreatedAt BIGINT,
    UpdatedAt BIGINT,
    DeletedAt BIGINT,
    Content TEXT,
    Tags TEXT,
    Embedding float_vector knn_type='hnsw' knn_dims='1024' hnsw_similarity='cosine'
)
ngram_len='2'
ngram_chars='U+3400..U+4DBF,U+4E00..U+9FFF,U+F900..U+FAFF'
dict='keywords'
min_word_len='1'
charset_type='utf-8'
enable_star='0';
