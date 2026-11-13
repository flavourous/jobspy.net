using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using JobSpy.Desktop.Models;

namespace JobSpy.Desktop.Services;

internal static class CodexJobPostingReview
{
    private const int BatchSize = 40;
    private const string PromptTemplate = """
read the job description at the end of this message and respond with a json object containing properties for the following data, using null only when the data is not in the job description:
employment_type: a concise label such as Permanent, Contract, Temporary, Full-time, Part-time, or Freelance. A standard 40-hour-per-week schedule with no limited-duration wording means Permanent.
location: a geographic area or remote-eligibility region stated in the posting; do not invent one.
salary: a numeric range (exactly £<number>-£<number>) or single value (exactly £<number>) preserving the amount and converting to GBP, interpreted as yearly.  choose only one salary, the largest, when multiple are listed.
pattern: exactly remote, onsite, hybrid, or hybrid:<days_per_week> with a decimal point
""";

    public static bool Review(
        IReadOnlyCollection<JobPosting> postings,
        Action<JobPosting, bool>? onPostingReviewed = null)
    {
        CodexTurnRunner.Session? session = null;
        var allSucceeded = true;

        try
        {
            var rows = postings.Select((posting, index) => CreateReviewRow(posting, index + 1)).ToArray();
            if (rows.Any(NeedsReview))
            {
                session = CodexTurnRunner.StartSession();
            }

            for (var offset = 0; offset < rows.Length; offset += BatchSize)
            {
                var batch = rows.Skip(offset).Take(BatchSize).ToArray();
                var outcomes = Task.WhenAll(batch.Select(row => ReviewOneAsync(row, session)))
                    .GetAwaiter()
                    .GetResult();

                foreach (var outcome in outcomes)
                {
                    if (!outcome.Succeeded)
                    {
                        allSucceeded = false;
                    }

                    onPostingReviewed?.Invoke(outcome.Row.Posting, outcome.Succeeded);
                }
            }

            return allSucceeded;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Codex job review failed: {exception.GetBaseException().Message}");
            throw;
        }
        finally
        {
            session?.Dispose();
        }
    }

    private static async Task<ReviewOutcome> ReviewOneAsync(ReviewRow row, CodexTurnRunner.Session? session)
    {
        if (!NeedsReview(row))
        {
            return new ReviewOutcome(row, true);
        }

        try
        {
            var response = await session!.RunAsync(Environment.CurrentDirectory, BuildPrompt(row)).ConfigureAwait(false);
            if (response is null)
            {
                Debug.WriteLine($"Codex job review {row.JobId} turn did not complete.");
                return new ReviewOutcome(row, false);
            }

            if (!TryReadResult(response, out var result, out var error))
            {
                Debug.WriteLine($"Codex job review {row.JobId} returned invalid JSON: {error}");
                return new ReviewOutcome(row, false);
            }

            ApplyResult(row, result!);
            return new ReviewOutcome(row, true);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Codex job review failed for {row.JobId}: {exception.GetBaseException().Message}");
            return new ReviewOutcome(row, false);
        }
    }

    private static string BuildPrompt(ReviewRow row)
    {
        var description = string.IsNullOrWhiteSpace(row.Posting.Description)
            ? row.Posting.Title ?? string.Empty
            : row.Posting.Description;
        return $"{PromptTemplate}\n\n———\n\n{description}";
    }

    private static bool TryReadResult(string json, out ReviewResult? result, out string? error)
    {
        result = null;
        error = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "result must be a JSON object";
                return false;
            }

            var names = root.EnumerateObject().Select(property => property.Name).ToArray();
            var expectedNames = new[] { "employment_type", "location", "salary", "pattern" };
            if (names.Length != expectedNames.Length || expectedNames.Any(name => !names.Contains(name, StringComparer.Ordinal)))
            {
                error = "result must contain exactly employment_type, location, salary, and pattern";
                return false;
            }

            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    error = $"{property.Name} must be a string or null";
                    return false;
                }
            }

            result = JsonSerializer.Deserialize<ReviewResult>(json);
            if (result is null)
            {
                error = "result could not be read";
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            error = "result is not valid JSON";
            return false;
        }
    }

    private static void ApplyResult(ReviewRow row, ReviewResult result)
    {
        JobPostingEnricher.ApplyAiCorrection(
            row.Posting,
            row.CanEditEmploymentType ? result.EmploymentType : null,
            row.CanEditLocation ? result.Location : null,
            row.CanEditSalary ? result.Salary : null,
            row.CanEditWorkPattern ? result.Pattern : null);
    }

    private static ReviewRow CreateReviewRow(JobPosting posting, int index) => new(
        $"job-{index:D5}",
        posting,
        string.IsNullOrWhiteSpace(posting.JobType),
        string.IsNullOrWhiteSpace(posting.Location),
        !posting.MinAmount.HasValue && !posting.MaxAmount.HasValue,
        !posting.IsRemote);

    private static bool NeedsReview(ReviewRow row) =>
        (!string.IsNullOrWhiteSpace(row.Posting.Title) || !string.IsNullOrWhiteSpace(row.Posting.Description))
        && (row.CanEditEmploymentType || row.CanEditLocation || row.CanEditSalary || row.CanEditWorkPattern);

    private sealed class ReviewResult
    {
        [JsonPropertyName("employment_type")]
        public string? EmploymentType { get; init; }

        [JsonPropertyName("location")]
        public string? Location { get; init; }

        [JsonPropertyName("salary")]
        public string? Salary { get; init; }

        [JsonPropertyName("pattern")]
        public string? Pattern { get; init; }
    }

    private sealed record ReviewRow(
        string JobId,
        JobPosting Posting,
        bool CanEditEmploymentType,
        bool CanEditLocation,
        bool CanEditSalary,
        bool CanEditWorkPattern);

    private sealed record ReviewOutcome(ReviewRow Row, bool Succeeded);
}
