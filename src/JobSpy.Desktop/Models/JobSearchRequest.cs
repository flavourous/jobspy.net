namespace JobSpy.Desktop.Models;

public sealed record JobSearchRequest(string SearchTerm, string Location, string[] Sites);

public sealed record JobSearchProgress(
	string Phase,
	string Site,
	int Count,
	int? Total = null,
	double? RetryAfterSeconds = null,
	string SearchTerm = "",
	string? Error = null);