using System.Collections.Generic;
using JobSpy.Desktop.Models;

namespace JobSpy.Desktop.Services;

public interface IJobSearchService
{
    IReadOnlyList<JobPosting> Search(JobSearchRequest request);
}