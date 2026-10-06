using System;
using System.Collections.Generic;
using JobSpy.Desktop.Models;

namespace JobSpy.Desktop.Repositories;

public interface IJobRepository
{
    IReadOnlyList<JobPosting> GetAll();
    IReadOnlyList<JobSearchSnapshot> GetHistory();
    IReadOnlyList<JobPosting> GetJobsForSnapshot(string snapshotId);
    IReadOnlyList<JobObservation> GetObservationsForSnapshot(string snapshotId);
    IReadOnlyList<JobObservation> GetAllObservations();
    JobSearchSnapshot RecordScan(IEnumerable<JobPosting> postings, string searchProfile, DateTime capturedAtUtc, bool isDemo = false);
    void SetStatus(string jobId, string status);
    void SetRelocationRequired(string jobId, bool requiresRelocation);
}