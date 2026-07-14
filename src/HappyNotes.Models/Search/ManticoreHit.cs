namespace HappyNotes.Models.Search;

public class ManticoreHit
{
    public long _id { get; set; }
    public long _score { get; set; }

    /// <summary>Distance to the query vector for KNN searches (lower = closer). Absent/0 for keyword searches.</summary>
    public double _knn_dist { get; set; }

    public required ManticoreSource _source { get; set; }
}
