using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using JobSpy.Desktop.Models;
using Python.Runtime;

namespace JobSpy.Desktop.Services;

public sealed class PythonJobSpyService : IJobSearchService
{
    private static readonly object RuntimeLock = new();
    private static bool _runtimeInitialized;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public IReadOnlyList<JobPosting> Search(JobSearchRequest request, IProgress<JobSearchProgress>? progress = null)
    {
        EnsurePythonRuntime();
        using (Py.GIL())
        {
            using var jobspyModule = Py.Import("jobspy");
            using var pythonSites = new PyList();
            foreach (var site in request.Sites)
            {
                using var pythonSite = new PyString(site);
                pythonSites.Append(pythonSite);
            }

            dynamic jobspy = jobspyModule;
            Action<string> progressCallback = payload =>
            {
                var update = JsonSerializer.Deserialize<JobSearchProgress>(payload, SerializerOptions);
                if (update is not null)
                {
                    progress?.Report(update with { SearchTerm = request.SearchTerm });
                }
            };
            using var pythonProgressCallback = PyObject.FromManagedObject(progressCallback);
            using var jobs = (PyObject)jobspy.scrape_jobs(
                site_name: pythonSites,
                search_term: request.SearchTerm,
                location: string.IsNullOrWhiteSpace(request.Location) ? null : request.Location,
                country_indeed: "UK",
                fetch_description: true,
                results_wanted: 1000,
                progress_callback: pythonProgressCallback,
                verbose: 0);
            dynamic dataFrame = jobs;
            using var jsonResult = (PyObject)dataFrame.to_json(orient: "records", date_format: "iso");

            var postings = JsonSerializer.Deserialize<List<JobPosting>>(jsonResult.As<string>(), SerializerOptions)
                ?? new List<JobPosting>();

            return [..postings];
        }
    }

    private static void EnsurePythonRuntime()
    {
        lock (RuntimeLock)
        {
            if (_runtimeInitialized)
            {
                return;
            }

            var pythonDll = Environment.GetEnvironmentVariable("PYTHONNET_PYDLL");
            if (string.IsNullOrWhiteSpace(pythonDll))
            {
                throw new InvalidOperationException(
                    "The embedded Python runtime was not configured during application startup.");
            }

            Runtime.PythonDLL = pythonDll;
            PythonEngine.Initialize();
            using (Py.GIL())
            {
                using var pythonSystem = Py.Import("sys");
                using var pythonPath = pythonSystem.GetAttr("path");
                foreach (var importPath in (Environment.GetEnvironmentVariable("PYTHONPATH") ?? string.Empty)
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    using var path = new PyString(importPath);
                    using var appendResult = pythonPath.InvokeMethod("append", path);
                }
            }

            PythonEngine.BeginAllowThreads();
            _runtimeInitialized = true;
        }
    }
}