namespace JobSpy.Desktop.Models;

public sealed record JobSearchRequest(string SearchTerm, string Location, string[] Sites);