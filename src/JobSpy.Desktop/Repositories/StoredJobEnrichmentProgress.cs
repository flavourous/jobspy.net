using System.Collections.Generic;
using JobSpy.Desktop.Models;

namespace JobSpy.Desktop.Repositories;

public sealed record StoredJobEnrichmentProgress(
    int Completed,
    int Total,
    int Failed,
    bool IsComplete,
    IReadOnlyList<JobPosting> UpdatedPostings,
    string? ErrorMessage = null)
{
    public double Percent => Total == 0 ? 100 : Completed * 100d / Total;
}
