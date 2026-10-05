using LiteDB;

namespace JobSpy.Desktop.Models;

public sealed class JobObservation
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string SnapshotId { get; set; } = string.Empty;

    public string JobId { get; set; } = string.Empty;

    public string SearchTerm { get; set; } = string.Empty;
}