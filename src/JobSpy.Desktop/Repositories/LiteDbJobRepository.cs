using System.Collections.Generic;
using System;
using System.Linq;
using JobSpy.Desktop.Models;
using JobSpy.Desktop.Services;
using LiteDB;

namespace JobSpy.Desktop.Repositories;

public sealed class LiteDbJobRepository : IJobRepository, System.IDisposable
{
    private readonly LiteDatabase _database;
    private readonly ILiteCollection<JobPosting> _postings;
    private readonly ILiteCollection<JobSearchSnapshot> _snapshots;
    private readonly ILiteCollection<JobObservation> _observations;

    public LiteDbJobRepository(string databasePath)
    {
        _database = new LiteDatabase(databasePath);
        _postings = _database.GetCollection<JobPosting>("job_postings");
        _snapshots = _database.GetCollection<JobSearchSnapshot>("job_search_snapshots");
        _observations = _database.GetCollection<JobObservation>("job_observations");
        _postings.EnsureIndex(posting => posting.JobUrl);
        _snapshots.EnsureIndex(snapshot => snapshot.CapturedAtUtc);
        _observations.EnsureIndex(observation => observation.SnapshotId);
        EnrichStoredPostings();
    }

    public IReadOnlyList<JobPosting> GetAll() => _postings.FindAll()
        .OrderByDescending(posting => posting.IsStarred)
        .ThenByDescending(posting => posting.LastSeenUtc)
        .ToList();

    public IReadOnlyList<JobSearchSnapshot> GetHistory() => _snapshots.FindAll()
        .OrderBy(snapshot => snapshot.CapturedAtUtc)
        .ToList();

    public IReadOnlyList<JobPosting> GetJobsForSnapshot(string snapshotId)
    {
        var jobs = _observations.Find(observation => observation.SnapshotId == snapshotId)
            .Select(observation => _postings.FindById(observation.JobId))
            .Where(posting => posting is not null)
            .Cast<JobPosting>()
            .DistinctBy(posting => posting.Id, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(posting => posting.IsStarred)
            .ThenBy(posting => posting.Company)
            .ToList();
        return jobs;
    }

    public IReadOnlyList<JobObservation> GetObservationsForSnapshot(string snapshotId) => _observations
        .Find(observation => observation.SnapshotId == snapshotId)
        .ToList();

    public JobSearchSnapshot RecordScan(IEnumerable<JobPosting> postings, string searchProfile, DateTime capturedAtUtc, bool isDemo = false)
    {
        var captured = capturedAtUtc.Kind == DateTimeKind.Utc ? capturedAtUtc : capturedAtUtc.ToUniversalTime();
        var matches = postings
            .Where(posting => posting is not null)
            .Select(posting => (Posting: posting, Id: BuildId(posting)))
            .ToList();
        var incomingById = matches
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToDictionary(item => item.Id, item => item.Posting, StringComparer.OrdinalIgnoreCase);
        var searchTermsById = matches
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(match => match.Posting.SearchTerm)
                    .Where(term => !string.IsNullOrWhiteSpace(term))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var snapshot = new JobSearchSnapshot
        {
            Id = Guid.NewGuid().ToString("N"),
            CapturedAtUtc = captured,
            SearchProfile = searchProfile,
            AvailableJobCount = incomingById.Count,
            IsDemo = isDemo,
        };
        var annualSalaries = incomingById.Values
            .Select(posting => posting.AnnualSalaryGbp)
            .Where(salary => salary.HasValue)
            .Select(salary => salary!.Value)
            .OrderBy(salary => salary)
            .ToArray();
        snapshot.SalaryJobCount = annualSalaries.Length;
        if (annualSalaries.Length > 0)
        {
            var middle = annualSalaries.Length / 2;
            snapshot.MedianAnnualSalaryGbp = annualSalaries.Length % 2 == 0
                ? (annualSalaries[middle - 1] + annualSalaries[middle]) / 2
                : annualSalaries[middle];
        }

        _database.BeginTrans();
        try
        {
            var existingById = _postings.FindAll().ToDictionary(posting => posting.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var (id, incoming) in incomingById)
            {
                existingById.TryGetValue(id, out var existing);
                incoming.Id = id;
                incoming.FirstSeenUtc = existing?.FirstSeenUtc ?? captured;
                incoming.LastSeenUtc = captured;
                incoming.DisappearedAtUtc = null;
                incoming.IsStarred = existing?.IsStarred ?? false;
                incoming.RequiresRelocation = existing is not null
                    && string.Equals(existing.Location, incoming.Location, StringComparison.OrdinalIgnoreCase)
                    ? existing.RequiresRelocation ?? incoming.RequiresRelocation
                    : incoming.RequiresRelocation;
                JobPostingEnricher.Enrich(incoming);
                if (incoming.EnrichedSalary is null && existing?.EnrichmentVersion == JobPostingEnricher.CurrentVersion)
                {
                    incoming.EnrichedSalary = existing.EnrichedSalary;
                }

                if (incoming.EnrichedType is null && existing?.EnrichmentVersion == JobPostingEnricher.CurrentVersion)
                {
                    incoming.EnrichedType = existing.EnrichedType;
                }

                _postings.Upsert(incoming);
                var searchTerms = searchTermsById[id];
                if (searchTerms.Length == 0)
                {
                    _observations.Upsert(new JobObservation
                    {
                        Id = $"{snapshot.Id}|{id}",
                        SnapshotId = snapshot.Id,
                        JobId = id,
                    });
                }
                else
                {
                    foreach (var searchTerm in searchTerms)
                    {
                        _observations.Upsert(new JobObservation
                        {
                            Id = $"{snapshot.Id}|{id}|{searchTerm}",
                            SnapshotId = snapshot.Id,
                            JobId = id,
                            SearchTerm = searchTerm!,
                        });
                    }
                }
            }

            foreach (var existing in existingById.Values)
            {
                if (existing.DisappearedAtUtc is null && !incomingById.ContainsKey(existing.Id))
                {
                    existing.DisappearedAtUtc = captured;
                    _postings.Upsert(existing);
                }
            }

            _snapshots.Insert(snapshot);
            _database.Commit();
            return snapshot;
        }
        catch
        {
            _database.Rollback();
            throw;
        }
    }

    public void SetStarred(string jobId, bool isStarred)
    {
        var posting = _postings.FindById(jobId);
        if (posting is null)
        {
            return;
        }

        posting.IsStarred = isStarred;
        _postings.Update(posting);
    }

    public void SetRelocationRequired(string jobId, bool requiresRelocation)
    {
        var posting = _postings.FindById(jobId);
        if (posting is null)
        {
            return;
        }

        posting.RequiresRelocation = requiresRelocation;
        _postings.Update(posting);
    }

    private static string BuildId(JobPosting posting) => string.IsNullOrWhiteSpace(posting.JobUrl)
        ? $"{posting.Site}|{posting.Company}|{posting.Title}|{posting.DatePosted}"
        : posting.JobUrl;

    private void EnrichStoredPostings()
    {
        var outdatedPostings = _postings.Find(posting => posting.EnrichmentVersion < JobPostingEnricher.CurrentVersion)
            .ToArray();
        if (outdatedPostings.Length == 0)
        {
            return;
        }

        _database.BeginTrans();
        try
        {
            foreach (var posting in outdatedPostings)
            {
                JobPostingEnricher.Enrich(posting);
                _postings.Update(posting);
            }

            _database.Commit();
        }
        catch
        {
            _database.Rollback();
            throw;
        }
    }

    public void Dispose() => _database.Dispose();
}