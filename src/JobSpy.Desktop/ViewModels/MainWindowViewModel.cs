using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using JobSpy.Desktop.Models;
using JobSpy.Desktop.Repositories;
using JobSpy.Desktop.Services;
using ReactiveUI;

namespace JobSpy.Desktop.ViewModels;

public sealed class LocationFilterOption
{
    public LocationFilterOption(string key, string label, bool isRemote = false)
    {
        Key = key;
        Label = label;
        IsRemote = isRemote;
    }

    public string Key { get; }

    public string Label { get; }

    public bool IsRemote { get; }
}

internal sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IReadOnlyList<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public sealed class ScanProgressRow : ReactiveObject
{
    private string _state = "Waiting";
    private string _results = "Waiting";
    private bool _isIndeterminate = true;
    private double _progressValue;

    public ScanProgressRow(string searchTerm, string site)
    {
        SearchTerm = searchTerm;
        Site = site;
    }

    public string SearchTerm { get; }

    public string Site { get; }

    public string SiteLabel => Site switch
    {
        "indeed" => "Indeed",
        "linkedin" => "LinkedIn",
        "glassdoor" => "Glassdoor",
        _ => Site,
    };

    public string State
    {
        get => _state;
        private set => this.RaiseAndSetIfChanged(ref _state, value);
    }

    public string Results
    {
        get => _results;
        private set => this.RaiseAndSetIfChanged(ref _results, value);
    }

    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set => this.RaiseAndSetIfChanged(ref _isIndeterminate, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => this.RaiseAndSetIfChanged(ref _progressValue, value);
    }

    public bool IsFinished => State is "Complete" or "Failed";

    public void Reset()
    {
        State = "Waiting";
        Results = "Waiting";
        IsIndeterminate = true;
        ProgressValue = 0;
        this.RaisePropertyChanged(nameof(IsFinished));
    }

    public void Apply(JobSearchProgress progress)
    {
        var total = progress.Total.GetValueOrDefault();
        var hasTotal = total > 0;
        var totalLabel = hasTotal ? $"/{total:N0}" : string.Empty;
        IsIndeterminate = !hasTotal;
        ProgressValue = hasTotal ? Math.Clamp(progress.Count * 100d / total, 0, 100) : 0;
        Results = $"{progress.Count:N0}{totalLabel} results";

        switch (progress.Phase.ToLowerInvariant())
        {
            case "searching":
                State = "Starting";
                Results = "Waiting for results";
                IsIndeterminate = true;
                ProgressValue = 0;
                break;
            case "reading":
                State = "Reading";
                break;
            case "backoff":
                State = $"Backoff {progress.RetryAfterSeconds.GetValueOrDefault():0.#}s";
                break;
            case "complete":
                State = "Complete";
                Results = $"{progress.Count:N0} results";
                IsIndeterminate = false;
                ProgressValue = 100;
                break;
            case "failed":
                State = "Failed";
                Results = progress.Error ?? "Search failed";
                IsIndeterminate = false;
                ProgressValue = 0;
                break;
        }

        this.RaisePropertyChanged(nameof(IsFinished));
    }
}

public sealed class MainWindowViewModel : ViewModelBase
{
    private static readonly string[] SearchTerms =
    {
        "Tech Lead C#",
        "Lead Software Engineer C#",
        "Senior Software Engineer C#",
        "Principal Software Engineer C#",
        "Staff Software Engineer C#",
        "Development Manager Software C#",
    };

    private static readonly string[] SearchSites = { "indeed", "linkedin", "glassdoor" };

    private const string SearchLocation = "United Kingdom";
    private readonly IJobRepository _repository;
    private readonly IJobSearchService _searchService;
    private readonly BatchObservableCollection<JobOpportunityViewModel> _filteredJobs;
    private SnapshotHistoryData[] _snapshotHistory = Array.Empty<SnapshotHistoryData>();
    private Dictionary<string, PostingFacts> _postingFactsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SnapshotChartPoint> _chartPointsBySnapshotId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string SearchTerm, string Site), int> _responseCounts = new();
    private string _statusMessage;
    private bool _isSearching;
    private bool _isScanProgressOpen;
    private bool _canCloseScanProgress;
    private int _finishedSearchTasks;
    private SnapshotChartPoint? _selectedPoint;
    private JobOpportunityViewModel? _selectedJob;
    private int _resultCount;
    private bool _hasNoHistory;
    private bool _hasNoJobs;
    private bool _hasNoFilteredJobs;
    private LocationFilterOption? _selectedLocationFilter;
    private LocationFilterOption? _selectedSourceFilter;
    private LocationFilterOption? _selectedStatusFilter;
    private bool _isNewTodayFilterEnabled;
    private bool _isDotNetLanguageFilterEnabled;
    private string _medianSalaryLabel = "No salary data";
    private string _salaryCoverageLabel = "No reported GBP salaries";
    private string _capturedAtLabel = "No scan selected";
    private string _snapshotKindLabel = string.Empty;
    private string _scanElapsedLabel = "Elapsed 00:00:00";

    public MainWindowViewModel(IJobRepository repository, IJobSearchService searchService)
    {
        _repository = repository;
        _searchService = searchService;
        ChartPoints = new ObservableCollection<SnapshotChartPoint>();
        Jobs = new ObservableCollection<JobOpportunityViewModel>();
        _filteredJobs = new BatchObservableCollection<JobOpportunityViewModel>();
        FilteredJobs = _filteredJobs;
        ScanProgressRows = new ObservableCollection<ScanProgressRow>(SearchTerms
            .SelectMany(term => SearchSites.Select(site => new ScanProgressRow(term, site))));
        LocationFilters = new ObservableCollection<LocationFilterOption>();
        SourceFilters = new ObservableCollection<LocationFilterOption>();
        StatusFilters = new ObservableCollection<LocationFilterOption>(new[]
        {
            new LocationFilterOption(string.Empty, "All statuses"),
        }.Concat(OpportunityStatus.All.Select(status => new LocationFilterOption(
            status,
            char.ToUpperInvariant(status[0]) + status[1..]))));
        _selectedStatusFilter = StatusFilters[0];

        SearchCommand = ReactiveCommand.CreateFromTask(
            async () =>
            {
                await SearchAsync();
                return Unit.Default;
            },
            this.WhenAnyValue(viewModel => viewModel.IsSearching).Select(isSearching => !isSearching));
        CloseScanProgressCommand = ReactiveCommand.Create(
            () =>
            {
                IsScanProgressOpen = false;
                return Unit.Default;
            },
            this.WhenAnyValue(viewModel => viewModel.CanCloseScanProgress));
        SelectSnapshotCommand = ReactiveCommand.Create<SnapshotChartPoint, Unit>(point =>
        {
            SelectedPoint = point;
            return Unit.Default;
        });
        SelectRunCommand = ReactiveCommand.Create<SalaryChartRunSelection, Unit>(selection =>
        {
            SelectedPoint = selection.Point;
            SelectedJob = Jobs.FirstOrDefault(job => string.Equals(
                job.Posting.Id,
                selection.JobId,
                StringComparison.OrdinalIgnoreCase));
            return Unit.Default;
        });
        OpenSelectedJobCommand = ReactiveCommand.Create(() =>
        {
            OpenSelectedJob();
            return Unit.Default;
        }, this.WhenAnyValue(viewModel => viewModel.SelectedJob)
            .Select(job => !string.IsNullOrWhiteSpace(job?.Posting.JobUrl)));

        PopulateHistory(_repository.GetHistory(), null);
        _statusMessage = ChartPoints.Count == 0
            ? "No scans yet. Run the profile to start building a history."
            : $"Loaded {ChartPoints.Count} saved scans from your local history.";
        _ = RefreshExistingRelocationStatusesAsync();
        _ = RefreshCurrencyRatesAsync();
    }

    public ObservableCollection<SnapshotChartPoint> ChartPoints { get; }

    public ObservableCollection<JobOpportunityViewModel> Jobs { get; }

    public ObservableCollection<JobOpportunityViewModel> FilteredJobs { get; }

    public ObservableCollection<ScanProgressRow> ScanProgressRows { get; }

    public ObservableCollection<LocationFilterOption> LocationFilters { get; }

    public ObservableCollection<LocationFilterOption> SourceFilters { get; }

    public ObservableCollection<LocationFilterOption> StatusFilters { get; }

    public ReactiveCommand<ReactiveUI.Primitives.RxVoid, Unit> SearchCommand { get; }
    public ReactiveCommand<ReactiveUI.Primitives.RxVoid, Unit> CloseScanProgressCommand { get; }

    public ReactiveCommand<SnapshotChartPoint, Unit> SelectSnapshotCommand { get; }

    public ReactiveCommand<SalaryChartRunSelection, Unit> SelectRunCommand { get; }

    public ReactiveCommand<ReactiveUI.Primitives.RxVoid, Unit> OpenSelectedJobCommand { get; }

    public string ScanProgressSummary => $"{_finishedSearchTasks} / {ScanProgressRows.Count} tasks finished";

    public string ResponseCountLabel => $"{_responseCounts.Values.Sum():N0} responses";

    public string SearchTermsLabel => string.Join("  ·  ", SearchTerms);

    public string SearchLocationLabel => "United Kingdom: remote and on-site roles nationwide";

    public string StatusMessage
    {
        get => _statusMessage;
        private set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set => this.RaiseAndSetIfChanged(ref _isSearching, value);
    }

    public bool IsScanProgressOpen
    {
        get => _isScanProgressOpen;
        private set => this.RaiseAndSetIfChanged(ref _isScanProgressOpen, value);
    }

    public bool CanCloseScanProgress
    {
        get => _canCloseScanProgress;
        private set => this.RaiseAndSetIfChanged(ref _canCloseScanProgress, value);
    }

    public string ScanElapsedLabel
    {
        get => _scanElapsedLabel;
        private set => this.RaiseAndSetIfChanged(ref _scanElapsedLabel, value);
    }

    public string SearchConfigurationLabel => $"{SearchTerms.Length} profiles · {SearchSites.Length} boards";

    public SnapshotChartPoint? SelectedPoint
    {
        get => _selectedPoint;
        private set
        {
            if (_selectedPoint == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedPoint, value);
            foreach (var point in ChartPoints)
            {
                point.IsSelected = point == value;
            }

            LoadSelectedSnapshot(value?.Snapshot);
        }
    }

    public JobOpportunityViewModel? SelectedJob
    {
        get => _selectedJob;
        set
        {
            if (_selectedJob == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedJob, value);
            this.RaisePropertyChanged(nameof(SelectedJobTitle));
            this.RaisePropertyChanged(nameof(SelectedJobCompany));
            this.RaisePropertyChanged(nameof(SelectedJobSalaryLabel));
            this.RaisePropertyChanged(nameof(SelectedJobLocation));
            this.RaisePropertyChanged(nameof(SelectedJobSiteLabel));
            this.RaisePropertyChanged(nameof(SelectedJobDatePostedLabel));
            this.RaisePropertyChanged(nameof(SelectedJobTrackedSinceLabel));
            this.RaisePropertyChanged(nameof(SelectedJobLastSeenLabel));
            this.RaisePropertyChanged(nameof(SelectedJobAvailabilityLabel));
            this.RaisePropertyChanged(nameof(SelectedJobDescription));
        }
    }

    public string SelectedJobTitle => SelectedJob?.Title ?? "Select an opportunity";

    public string SelectedJobCompany => SelectedJob?.Company ?? string.Empty;

    public string SelectedJobSalaryLabel => SelectedJob?.SalaryLabel ?? string.Empty;

    public string SelectedJobLocation => SelectedJob?.Location ?? string.Empty;

    public string SelectedJobSiteLabel => SelectedJob?.SiteLabel ?? string.Empty;

    public string SelectedJobDatePostedLabel => SelectedJob?.DatePostedLabel ?? string.Empty;

    public string SelectedJobTrackedSinceLabel => SelectedJob?.TrackedSinceLabel ?? string.Empty;

    public string SelectedJobLastSeenLabel => SelectedJob?.LastSeenLabel ?? string.Empty;

    public string SelectedJobAvailabilityLabel => SelectedJob?.AvailabilityLabel ?? string.Empty;

    public string SelectedJobDescription => SelectedJob?.Description ?? "Choose an opportunity to view its details.";

    public int ResultCount
    {
        get => _resultCount;
        private set => this.RaiseAndSetIfChanged(ref _resultCount, value);
    }

    public bool HasNoHistory
    {
        get => _hasNoHistory;
        private set => this.RaiseAndSetIfChanged(ref _hasNoHistory, value);
    }

    public bool HasNoJobs
    {
        get => _hasNoJobs;
        private set => this.RaiseAndSetIfChanged(ref _hasNoJobs, value);
    }

    public bool HasNoFilteredJobs
    {
        get => _hasNoFilteredJobs;
        private set => this.RaiseAndSetIfChanged(ref _hasNoFilteredJobs, value);
    }

    public LocationFilterOption? SelectedLocationFilter
    {
        get => _selectedLocationFilter;
        set
        {
            if (_selectedLocationFilter == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedLocationFilter, value);
            RefreshFilteredViews();
        }
    }

    public LocationFilterOption? SelectedSourceFilter
    {
        get => _selectedSourceFilter;
        set
        {
            if (_selectedSourceFilter == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedSourceFilter, value);
            RefreshFilteredViews();
        }
    }

    public LocationFilterOption? SelectedStatusFilter
    {
        get => _selectedStatusFilter;
        set
        {
            if (_selectedStatusFilter == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedStatusFilter, value);
            RefreshFilteredViews();
        }
    }

    public bool IsNewTodayFilterEnabled
    {
        get => _isNewTodayFilterEnabled;
        set
        {
            if (_isNewTodayFilterEnabled == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _isNewTodayFilterEnabled, value);
            RefreshFilteredViews();
        }
    }

    public bool IsDotNetLanguageFilterEnabled
    {
        get => _isDotNetLanguageFilterEnabled;
        set
        {
            if (_isDotNetLanguageFilterEnabled == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _isDotNetLanguageFilterEnabled, value);
            RefreshFilteredViews();
        }
    }

    public string MedianSalaryLabel
    {
        get => _medianSalaryLabel;
        private set => this.RaiseAndSetIfChanged(ref _medianSalaryLabel, value);
    }

    public string SalaryCoverageLabel
    {
        get => _salaryCoverageLabel;
        private set => this.RaiseAndSetIfChanged(ref _salaryCoverageLabel, value);
    }

    public string CapturedAtLabel
    {
        get => _capturedAtLabel;
        private set => this.RaiseAndSetIfChanged(ref _capturedAtLabel, value);
    }

    public string SnapshotKindLabel
    {
        get => _snapshotKindLabel;
        private set => this.RaiseAndSetIfChanged(ref _snapshotKindLabel, value);
    }

    private async Task SearchAsync()
    {
        IsScanProgressOpen = true;
        CanCloseScanProgress = false;
        IsSearching = true;
        var scanStopwatch = Stopwatch.StartNew();
        using var elapsedCancellation = new CancellationTokenSource();
        var elapsedUpdater = UpdateScanElapsedLabelAsync(scanStopwatch, elapsedCancellation.Token);
        foreach (var row in ScanProgressRows)
        {
            row.Reset();
        }
        _responseCounts.Clear();
        _finishedSearchTasks = 0;
        this.RaisePropertyChanged(nameof(ScanProgressSummary));
        this.RaisePropertyChanged(nameof(ResponseCountLabel));
        StatusMessage = $"Scanning {SearchTerms.Length} role searches across {SearchSites.Length} boards...";
        IProgress<JobSearchProgress> progress = new Progress<JobSearchProgress>(ApplySearchProgress);
        try
        {
            var result = await Task.Run(async () =>
            {
                var searchTasks = SearchTerms.SelectMany(term => SearchSites.Select(site => Task.Run(() =>
                {
                    progress.Report(new JobSearchProgress("searching", site, 0, SearchTerm: term));
                    try
                    {
                        var postings = _searchService.Search(
                            new JobSearchRequest(term, SearchLocation, [site]),
                            progress);
                        foreach (var posting in postings)
                        {
                            posting.SearchTerm = term;
                        }

                        progress.Report(new JobSearchProgress(
                            "complete",
                            site,
                            postings.Count,
                            postings.Count,
                            SearchTerm: term));
                        return postings;
                    }
                    catch (Exception exception)
                    {
                        progress.Report(new JobSearchProgress(
                            "failed",
                            site,
                            0,
                            SearchTerm: term,
                            Error: exception.GetBaseException().Message));
                        throw;
                    }
                }))).ToArray();
                var resultSets = await Task.WhenAll(searchTasks);
                var found = resultSets.SelectMany(postings => postings).ToList();

                var snapshot = _repository.RecordScan(found, string.Join(", ", SearchTerms), DateTime.UtcNow);
                var snapshotJobs = _repository.GetJobsForSnapshot(snapshot.Id);
                await ResolveRelocationStatusesAsync(snapshotJobs);
                foreach (var posting in snapshotJobs.Where(posting => posting.RequiresRelocation.HasValue))
                {
                    _repository.SetRelocationRequired(posting.Id, posting.RequiresRelocation!.Value);
                }

                return (Snapshot: snapshot, History: _repository.GetHistory());
            });

            PopulateHistory(result.History, result.Snapshot.Id);
            StatusMessage = $"Captured {result.Snapshot.AvailableJobCount} available roles; {result.Snapshot.SalaryJobCount} report a comparable GBP salary.";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Scan failed: {exception.GetBaseException().Message}";
        }
        finally
        {
            scanStopwatch.Stop();
            elapsedCancellation.Cancel();
            await elapsedUpdater;
            IsSearching = false;
            CanCloseScanProgress = true;
        }
    }

    private async Task UpdateScanElapsedLabelAsync(Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        while (true)
        {
            ScanElapsedLabel = FormatElapsed(stopwatch.Elapsed);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        ScanElapsedLabel = FormatElapsed(stopwatch.Elapsed);
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        $"Elapsed {(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

    private void ApplySearchProgress(JobSearchProgress progress)
    {
        if (progress.Phase is "reading" or "backoff" or "complete")
        {
            var key = (progress.SearchTerm, progress.Site);
            if (!_responseCounts.TryGetValue(key, out var previousCount)
                || progress.Count > previousCount)
            {
                _responseCounts[key] = progress.Count;
                this.RaisePropertyChanged(nameof(ResponseCountLabel));
            }
        }

        var row = ScanProgressRows.FirstOrDefault(candidate =>
            string.Equals(candidate.SearchTerm, progress.SearchTerm, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Site, progress.Site, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return;
        }

        var wasFinished = row.IsFinished;
        row.Apply(progress);
        if (!wasFinished && row.IsFinished)
        {
            _finishedSearchTasks++;
            this.RaisePropertyChanged(nameof(ScanProgressSummary));
        }
    }

    private void PopulateHistory(System.Collections.Generic.IReadOnlyList<JobSearchSnapshot> history, string? selectedSnapshotId)
    {
        var postingsById = _repository.GetAll()
            .ToDictionary(posting => posting.Id, StringComparer.OrdinalIgnoreCase);
        _postingFactsById = postingsById.ToDictionary(
            pair => pair.Key,
            pair => new PostingFacts(
                JobLanguageFilter.IsRelevant(pair.Value.Description),
                pair.Value.AnnualSalaryGbp,
                IsManagementRole(pair.Value.Title),
                JobLocationClassifier.NeedsRelocation(pair.Value)),
            StringComparer.OrdinalIgnoreCase);
        var observationsBySnapshot = _repository.GetAllObservations()
            .GroupBy(observation => observation.SnapshotId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        _snapshotHistory = history.Select(snapshot =>
        {
            var observations = observationsBySnapshot.GetValueOrDefault(snapshot.Id) ?? Array.Empty<JobObservation>();
            var snapshotPostings = observations
                .Select(observation => postingsById.GetValueOrDefault(observation.JobId))
                .Where(posting => posting is not null)
                .Cast<JobPosting>()
                .DistinctBy(posting => posting.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var searchTermsByJob = observations
                .Where(observation => !string.IsNullOrWhiteSpace(observation.SearchTerm))
                .GroupBy(observation => observation.JobId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(observation => observation.SearchTerm)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            return new SnapshotHistoryData(snapshot, snapshotPostings, searchTermsByJob);
        }).ToArray();

        ChartPoints.Clear();
        _chartPointsBySnapshotId.Clear();
        RebuildHistory(selectedSnapshotId);
    }

    private void RebuildHistory(string? selectedSnapshotId)
    {
        foreach (var snapshotData in _snapshotHistory)
        {
            var snapshot = snapshotData.Snapshot;
            var jobs = snapshotData.Postings
                .Where(MatchesPostingFilters)
                .ToArray();
            var searchTermsByJob = snapshotData.SearchTermsByJob;
            var salariesByJob = jobs.ToDictionary(
                posting => posting.Id,
                posting => _postingFactsById[posting.Id].AnnualSalaryGbp,
                StringComparer.OrdinalIgnoreCase);
            var salariesByTerm = jobs
                .Where(posting => salariesByJob[posting.Id].HasValue)
                .SelectMany(posting => searchTermsByJob.TryGetValue(posting.Id, out var terms)
                    ? terms.Select(term => (Term: term, Salary: salariesByJob[posting.Id]!.Value))
                    : Array.Empty<(string Term, decimal Salary)>())
                .GroupBy(item => item.Term, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => Median(group.Select(item => item.Salary).OrderBy(salary => salary).ToArray()), StringComparer.OrdinalIgnoreCase);
            var reportedSalaries = jobs
                .Select(posting => salariesByJob[posting.Id])
                .Where(salary => salary.HasValue)
                .Select(salary => salary!.Value)
                .OrderBy(salary => salary)
                .ToArray();
            var scanMedian = Median(reportedSalaries);
            var segments = jobs
                .Select(posting =>
                {
                    var facts = _postingFactsById[posting.Id];
                    var reportedSalary = salariesByJob[posting.Id];
                    var termMedian = searchTermsByJob.TryGetValue(posting.Id, out var terms)
                        ? Median(terms.Where(salariesByTerm.ContainsKey).SelectMany(term =>
                            salariesByTerm[term].HasValue ? new[] { salariesByTerm[term]!.Value } : Array.Empty<decimal>()).OrderBy(salary => salary).ToArray())
                        : null;
                    var salary = reportedSalary ?? termMedian ?? scanMedian;
                    return salary.HasValue
                        ? new SalaryChartSegment(
                            posting,
                            salary.Value,
                            !reportedSalary.HasValue,
                            facts.IsManagement,
                            facts.NeedsRelocation)
                        : null;
                })
                .Where(segment => segment is not null)
                .Cast<SalaryChartSegment>()
                .OrderBy(segment => segment.Posting.FirstSeenUtc)
                .ThenBy(segment => segment.Posting.Id, StringComparer.Ordinal)
                .ToArray();
            if (_chartPointsBySnapshotId.TryGetValue(snapshot.Id, out var point))
            {
                point.UpdateSegments(segments);
            }
            else
            {
                point = new SnapshotChartPoint(snapshot, segments, SelectSnapshot);
                _chartPointsBySnapshotId.Add(snapshot.Id, point);
                ChartPoints.Add(point);
            }
        }

        HasNoHistory = ChartPoints.Count == 0;
        SelectedPoint = ChartPoints.FirstOrDefault(point => point.Snapshot.Id == selectedSnapshotId)
            ?? ChartPoints.LastOrDefault();
    }

    private sealed record SnapshotHistoryData(
        JobSearchSnapshot Snapshot,
        JobPosting[] Postings,
        Dictionary<string, string[]> SearchTermsByJob);

    private sealed record PostingFacts(
        bool IsDotNetRelevant,
        decimal? AnnualSalaryGbp,
        bool IsManagement,
        bool NeedsRelocation);

    private void SelectSnapshot(SnapshotChartPoint point) => SelectedPoint = point;

    private async Task RefreshExistingRelocationStatusesAsync()
    {
        var postings = _repository.GetAll()
            .Where(posting => !posting.RequiresRelocation.HasValue)
            .ToArray();
        if (postings.Length == 0)
        {
            return;
        }

        await ResolveRelocationStatusesAsync(postings);
        var resolvedPostings = postings.Where(posting => posting.RequiresRelocation.HasValue).ToArray();
        foreach (var posting in resolvedPostings)
        {
            _repository.SetRelocationRequired(posting.Id, posting.RequiresRelocation!.Value);
        }

        if (resolvedPostings.Length == 0)
        {
            return;
        }

        var selectedSnapshotId = SelectedPoint?.Snapshot.Id;
        var selectedJobId = SelectedJob?.Posting.Id;
        PopulateHistory(_repository.GetHistory(), selectedSnapshotId);
        SelectedJob = Jobs.FirstOrDefault(job => string.Equals(
            job.Posting.Id,
            selectedJobId,
            StringComparison.OrdinalIgnoreCase)) ?? Jobs.FirstOrDefault();
    }

    private async Task RefreshCurrencyRatesAsync()
    {
        if (!await SalaryCurrencyConverter.RefreshRatesAsync())
        {
            return;
        }

        var selectedSnapshotId = SelectedPoint?.Snapshot.Id;
        var selectedJobId = SelectedJob?.Posting.Id;
        PopulateHistory(_repository.GetHistory(), selectedSnapshotId);
        SelectedJob = FilteredJobs.FirstOrDefault(job => string.Equals(
            job.Posting.Id,
            selectedJobId,
            StringComparison.OrdinalIgnoreCase)) ?? FilteredJobs.FirstOrDefault();
    }

    private static async Task ResolveRelocationStatusesAsync(System.Collections.Generic.IEnumerable<JobPosting> postings)
    {
        await Task.WhenAll(postings
            .Where(posting => !posting.RequiresRelocation.HasValue)
            .Select(async posting =>
            {
                posting.RequiresRelocation = await JobLocationClassifier.ResolveNeedsRelocationAsync(posting);
            }));
    }

    private static decimal? Median(decimal[] salaries)
    {
        if (salaries.Length == 0)
        {
            return null;
        }

        var middle = salaries.Length / 2;
        return salaries.Length % 2 == 0
            ? (salaries[middle - 1] + salaries[middle]) / 2
            : salaries[middle];
    }

    private static bool IsManagementRole(string? title)
    {
        var normalizedTitle = title ?? string.Empty;
        return normalizedTitle.Contains("manager", StringComparison.OrdinalIgnoreCase)
            || normalizedTitle.Contains("director", StringComparison.OrdinalIgnoreCase)
            || normalizedTitle.Contains("head of", StringComparison.OrdinalIgnoreCase)
            || normalizedTitle.Contains("chief", StringComparison.OrdinalIgnoreCase)
            || normalizedTitle.Contains("vice president", StringComparison.OrdinalIgnoreCase);
    }

    private void LoadSelectedSnapshot(JobSearchSnapshot? snapshot)
    {
        Jobs.Clear();
        var snapshotData = snapshot is null
            ? null
            : _snapshotHistory.FirstOrDefault(data => data.Snapshot.Id == snapshot.Id);
        if (snapshotData is null)
        {
            ResultCount = 0;
            MedianSalaryLabel = "No salary data";
            SalaryCoverageLabel = "No reported GBP salaries";
            CapturedAtLabel = "No scan selected";
            SnapshotKindLabel = string.Empty;
            SelectedJob = null;
            HasNoJobs = true;
            RefreshLocationFilters();
            RefreshSourceFilters();
            RefreshFilteredJobs();
            return;
        }

        var selectedSnapshot = snapshotData.Snapshot;
        var postings = snapshotData.Postings
            .OrderByDescending(posting => posting.AnnualSalaryGbp.HasValue)
            .ThenByDescending(posting => posting.AnnualSalaryGbp.GetValueOrDefault())
            .ThenBy(posting => posting.Company)
            .ToArray();
        foreach (var posting in postings)
        {
            Jobs.Add(new JobOpportunityViewModel(posting, SaveOpportunityStatus));
        }

        RefreshLocationFilters();
        RefreshSourceFilters();
        RefreshFilteredJobs();
        CapturedAtLabel = selectedSnapshot.CapturedAtUtc.ToLocalTime().ToString("ddd, dd MMM yyyy  HH:mm");
        SnapshotKindLabel = selectedSnapshot.IsDemo ? "DEMO HISTORY" : string.Empty;
        SelectedJob = FilteredJobs.FirstOrDefault();
        HasNoJobs = Jobs.Count == 0;
        if (HasNoJobs)
        {
            MedianSalaryLabel = "Not reported";
            SalaryCoverageLabel = "0 of 0 roles";
        }
    }

    private void RefreshFilteredJobs()
    {
        var filteredJobs = Jobs.Where(job => MatchesPostingFilters(job.Posting)).ToArray();
        _filteredJobs.ReplaceAll(filteredJobs);

        HasNoFilteredJobs = Jobs.Count > 0 && filteredJobs.Length == 0;
        ResultCount = filteredJobs.Length;
        if (Jobs.Count == 0)
        {
            return;
        }

        var reportedSalaries = filteredJobs
            .Select(job => job.Posting.AnnualSalaryGbp)
            .Where(salary => salary.HasValue)
            .Select(salary => salary!.Value)
            .OrderBy(salary => salary)
            .ToArray();
        var medianSalary = Median(reportedSalaries);
        MedianSalaryLabel = medianSalary.HasValue ? $"£{medianSalary.Value:N0}" : "Not reported";
        SalaryCoverageLabel = $"{reportedSalaries.Length} of {filteredJobs.Length} roles";
    }

    private bool MatchesPostingFilters(JobPosting posting) =>
        MatchesLocationFilter(posting, SelectedLocationFilter)
        && MatchesSourceFilter(posting, SelectedSourceFilter)
        && MatchesStatusFilter(posting, SelectedStatusFilter)
        && (!IsNewTodayFilterEnabled || WasAddedToHistoryToday(posting))
        && (!IsDotNetLanguageFilterEnabled || _postingFactsById.GetValueOrDefault(posting.Id)?.IsDotNetRelevant != false);

    private static bool MatchesStatusFilter(JobPosting posting, LocationFilterOption? filter) =>
        filter is null || filter.Key.Length == 0
        || string.Equals(posting.Status, filter.Key, StringComparison.OrdinalIgnoreCase);

    private void RefreshFilteredViews()
    {
        var selectedSnapshotId = SelectedPoint?.Snapshot.Id;
        var selectedJobId = SelectedJob?.Posting.Id;
        RebuildHistory(selectedSnapshotId);
        RefreshFilteredJobs();
        SelectedJob = FilteredJobs.FirstOrDefault(job => string.Equals(
            job.Posting.Id,
            selectedJobId,
            StringComparison.OrdinalIgnoreCase)) ?? FilteredJobs.FirstOrDefault();
    }

    private static bool WasAddedToHistoryToday(JobPosting posting) => posting.FirstSeenUtc != default
        && posting.FirstSeenUtc.ToLocalTime().Date == DateTime.Today;

    private void RefreshLocationFilters()
    {
        var previousKey = SelectedLocationFilter?.Key;
        var previousWasRemote = SelectedLocationFilter?.IsRemote ?? false;
        var remoteCount = Jobs.Count(job => IsRemotePosting(job.Posting));
        var cityOptions = Jobs
            .Where(job => !IsRemotePosting(job.Posting))
            .Select(job => GetLocationKey(job.Posting))
            .Where(location => location.Length > 0)
            .GroupBy(location => location, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Key: group.Key, Count: group.Count()))
            .OrderByDescending(location => location.Count)
            .ThenBy(location => location.Key, StringComparer.OrdinalIgnoreCase)
            .Select(location => new LocationFilterOption(
                location.Key,
                $"{FormatLocationLabel(location.Key)} ({location.Count})"))
            .ToArray();

        LocationFilters.Clear();
        LocationFilters.Add(new LocationFilterOption(string.Empty, $"All locations ({Jobs.Count})"));
        if (remoteCount > 0)
        {
            LocationFilters.Add(new LocationFilterOption("remote", $"Remote ({remoteCount})", isRemote: true));
        }

        foreach (var option in cityOptions)
        {
            LocationFilters.Add(option);
        }

        _selectedLocationFilter = LocationFilters.FirstOrDefault(option =>
            option.IsRemote == previousWasRemote
            && string.Equals(option.Key, previousKey, StringComparison.OrdinalIgnoreCase))
            ?? LocationFilters.FirstOrDefault();
        this.RaisePropertyChanged(nameof(SelectedLocationFilter));
    }

    private void RefreshSourceFilters()
    {
        var previousKey = SelectedSourceFilter?.Key;
        var sourceOptions = Jobs
            .Select(job => job.Posting.Site?.Trim())
            .Where(site => !string.IsNullOrWhiteSpace(site))
            .GroupBy(site => site!, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Key: group.Key, Count: group.Count()))
            .OrderByDescending(source => source.Count)
            .ThenBy(source => source.Key, StringComparer.OrdinalIgnoreCase)
            .Select(source => new LocationFilterOption(
                source.Key,
                $"{FormatSourceLabel(source.Key)} ({source.Count})"))
            .ToArray();

        SourceFilters.Clear();
        SourceFilters.Add(new LocationFilterOption(string.Empty, $"All sources ({Jobs.Count})"));
        foreach (var option in sourceOptions)
        {
            SourceFilters.Add(option);
        }

        _selectedSourceFilter = SourceFilters.FirstOrDefault(option =>
            string.Equals(option.Key, previousKey, StringComparison.OrdinalIgnoreCase))
            ?? SourceFilters.FirstOrDefault();
        this.RaisePropertyChanged(nameof(SelectedSourceFilter));
    }

    private static bool MatchesSourceFilter(JobPosting posting, LocationFilterOption? filter) =>
        filter is null || filter.Key.Length == 0
        || string.Equals(posting.Site?.Trim(), filter.Key, StringComparison.OrdinalIgnoreCase);

    private static string FormatSourceLabel(string source) => source switch
    {
        "zip_recruiter" => "ZipRecruiter",
        _ => source.Length == 0 ? source : char.ToUpperInvariant(source[0]) + source[1..],
    };

    private static bool MatchesLocationFilter(JobPosting posting, LocationFilterOption? filter)
    {
        if (filter is null || filter.Key.Length == 0)
        {
            return true;
        }

        return filter.IsRemote
            ? IsRemotePosting(posting)
            : string.Equals(GetLocationKey(posting), filter.Key, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRemotePosting(JobPosting posting) => posting.IsRemote
        || posting.Location?.Contains("remote", StringComparison.OrdinalIgnoreCase) == true;

    private static string GetLocationKey(JobPosting posting)
    {
        var location = posting.Location?.Trim();
        if (string.IsNullOrWhiteSpace(location))
        {
            return string.Empty;
        }

        var city = location.Split(',', 2)[0].Trim();
        return city.Equals("UK", StringComparison.OrdinalIgnoreCase)
            || city.Equals("United Kingdom", StringComparison.OrdinalIgnoreCase)
            ? "United Kingdom"
            : city;
    }

    private static string FormatLocationLabel(string location) => location == "United Kingdom"
        ? location
        : $"{location}, UK";

    private void OpenSelectedJob()
    {
        if (!Uri.TryCreate(SelectedJob?.Posting.JobUrl, UriKind.Absolute, out var jobUri))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(jobUri.AbsoluteUri) { UseShellExecute = true });
    }

    private void SaveOpportunityStatus(string jobId, string status)
    {
        _repository.SetStatus(jobId, status);
        if (SelectedStatusFilter is null
            || SelectedStatusFilter.Key.Length == 0
            || string.Equals(SelectedStatusFilter.Key, status, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RefreshFilteredViews();
    }
}