using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using JobSpy.Desktop.Models;
using JobSpy.Desktop.Repositories;
using JobSpy.Desktop.Services;
using ReactiveUI;

namespace JobSpy.Desktop.ViewModels;

public sealed class JobSiteOption : ReactiveObject
{
    private bool _isSelected;

    public JobSiteOption(string name, bool isSelected)
    {
        Name = name;
        _isSelected = isSelected;
    }

    public string Name { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}

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

public sealed class MainWindowViewModel : ViewModelBase
{
    private static readonly string[] SearchTerms =
    {
        "Lead .NET Engineer",
        "Principal Software Engineer C#",
        "Staff Backend Engineer .NET",
        "Distributed Systems Engineer .NET",
        "Engineering Manager Software",
    };

    private const string SearchLocation = "United Kingdom";
    private readonly IJobRepository _repository;
    private readonly IJobSearchService _searchService;
    private string _statusMessage;
    private bool _isSearching;
    private SnapshotChartPoint? _selectedPoint;
    private JobOpportunityViewModel? _selectedJob;
    private int _resultCount;
    private bool _hasNoHistory;
    private bool _hasNoJobs;
    private bool _hasNoFilteredJobs;
    private LocationFilterOption? _selectedLocationFilter;
    private string _medianSalaryLabel = "No salary data";
    private string _salaryCoverageLabel = "No reported GBP salaries";
    private string _capturedAtLabel = "No scan selected";
    private string _snapshotKindLabel = string.Empty;

    public MainWindowViewModel(IJobRepository repository, IJobSearchService searchService)
    {
        _repository = repository;
        _searchService = searchService;
        ChartPoints = new ObservableCollection<SnapshotChartPoint>();
        Jobs = new ObservableCollection<JobOpportunityViewModel>();
        FilteredJobs = new ObservableCollection<JobOpportunityViewModel>();
        LocationFilters = new ObservableCollection<LocationFilterOption>();
        Sites = new ObservableCollection<JobSiteOption>
        {
            new("indeed", true),
            new("linkedin", true),
            new("glassdoor", true),
            new("zip_recruiter", false),
            new("google", false),
        };

        SearchCommand = ReactiveCommand.CreateFromTask(
            async () =>
            {
                await SearchAsync();
                return Unit.Default;
            },
            this.WhenAnyValue(viewModel => viewModel.IsSearching).Select(isSearching => !isSearching));
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
    }

    public ObservableCollection<SnapshotChartPoint> ChartPoints { get; }

    public ObservableCollection<JobOpportunityViewModel> Jobs { get; }

    public ObservableCollection<JobOpportunityViewModel> FilteredJobs { get; }

    public ObservableCollection<LocationFilterOption> LocationFilters { get; }

    public ObservableCollection<JobSiteOption> Sites { get; }

    public ReactiveCommand<ReactiveUI.Primitives.RxVoid, Unit> SearchCommand { get; }

    public ReactiveCommand<SnapshotChartPoint, Unit> SelectSnapshotCommand { get; }

    public ReactiveCommand<SalaryChartRunSelection, Unit> SelectRunCommand { get; }

    public ReactiveCommand<ReactiveUI.Primitives.RxVoid, Unit> OpenSelectedJobCommand { get; }

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
            RefreshFilteredJobs();
            if (SelectedJob is null || !FilteredJobs.Contains(SelectedJob))
            {
                SelectedJob = FilteredJobs.FirstOrDefault();
            }
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
        var selectedSites = Sites.Where(site => site.IsSelected).Select(site => site.Name).ToArray();
        if (selectedSites.Length == 0)
        {
            StatusMessage = "Select at least one job board.";
            return;
        }

        IsSearching = true;
        StatusMessage = $"Scanning {SearchTerms.Length} role searches across {selectedSites.Length} boards...";
        try
        {
            var result = await Task.Run(async () =>
            {
                var found = SearchTerms
                    .SelectMany(term => _searchService
                        .Search(new JobSearchRequest(term, SearchLocation, selectedSites))
                        .Select(posting =>
                        {
                            posting.SearchTerm = term;
                            return posting;
                        }))
                    .ToList();
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
            IsSearching = false;
        }
    }

    private void PopulateHistory(System.Collections.Generic.IReadOnlyList<JobSearchSnapshot> history, string? selectedSnapshotId)
    {
        ChartPoints.Clear();
        foreach (var snapshot in history)
        {
            var jobs = _repository.GetJobsForSnapshot(snapshot.Id);
            var searchTermsByJob = _repository.GetObservationsForSnapshot(snapshot.Id)
                .Where(observation => !string.IsNullOrWhiteSpace(observation.SearchTerm))
                .GroupBy(observation => observation.JobId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(observation => observation.SearchTerm).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            var salariesByTerm = jobs
                .Where(posting => posting.AnnualSalaryGbp.HasValue)
                .SelectMany(posting => searchTermsByJob.TryGetValue(posting.Id, out var terms)
                    ? terms.Select(term => (Term: term, Salary: posting.AnnualSalaryGbp!.Value))
                    : Array.Empty<(string Term, decimal Salary)>())
                .GroupBy(item => item.Term, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => Median(group.Select(item => item.Salary).OrderBy(salary => salary).ToArray()), StringComparer.OrdinalIgnoreCase);
            var reportedSalaries = jobs
                .Select(posting => posting.AnnualSalaryGbp)
                .Where(salary => salary.HasValue)
                .Select(salary => salary!.Value)
                .OrderBy(salary => salary)
                .ToArray();
            var scanMedian = Median(reportedSalaries);
            var segments = jobs
                .Select(posting =>
                {
                    var reportedSalary = posting.AnnualSalaryGbp;
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
                            IsManagementRole(posting.Title),
                            JobLocationClassifier.NeedsRelocation(posting))
                        : null;
                })
                .Where(segment => segment is not null)
                .Cast<SalaryChartSegment>()
                .Where(segment => segment.Posting.IsStarred)
                .OrderBy(segment => segment.Posting.FirstSeenUtc)
                .ThenBy(segment => segment.Posting.Id, StringComparer.Ordinal)
                .ToArray();
            ChartPoints.Add(new SnapshotChartPoint(snapshot, segments, SelectSnapshot));
        }

        HasNoHistory = ChartPoints.Count == 0;
        SelectedPoint = ChartPoints.FirstOrDefault(point => point.Snapshot.Id == selectedSnapshotId)
            ?? ChartPoints.LastOrDefault();
    }

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
        if (snapshot is null)
        {
            ResultCount = 0;
            MedianSalaryLabel = "No salary data";
            SalaryCoverageLabel = "No reported GBP salaries";
            CapturedAtLabel = "No scan selected";
            SnapshotKindLabel = string.Empty;
            SelectedJob = null;
            HasNoJobs = true;
            RefreshLocationFilters();
            RefreshFilteredJobs();
            return;
        }

        var postings = _repository.GetJobsForSnapshot(snapshot.Id)
            .OrderByDescending(posting => posting.AnnualSalaryGbp.HasValue)
            .ThenByDescending(posting => posting.AnnualSalaryGbp.GetValueOrDefault())
            .ThenByDescending(posting => posting.IsStarred)
            .ThenBy(posting => posting.Company)
            .ToArray();
        foreach (var posting in postings)
        {
            Jobs.Add(new JobOpportunityViewModel(posting, SaveStarredState));
        }

        RefreshLocationFilters();
        RefreshFilteredJobs();

        var reportedSalaries = postings
            .Select(posting => posting.AnnualSalaryGbp)
            .Where(salary => salary.HasValue)
            .Select(salary => salary!.Value)
            .OrderBy(salary => salary)
            .ToArray();
        var medianSalary = Median(reportedSalaries);
        ResultCount = snapshot.AvailableJobCount;
        MedianSalaryLabel = medianSalary.HasValue
            ? $"£{medianSalary.Value:N0}"
            : "Not reported";
        SalaryCoverageLabel = $"{reportedSalaries.Length} of {snapshot.AvailableJobCount} roles";
        CapturedAtLabel = snapshot.CapturedAtUtc.ToLocalTime().ToString("ddd, dd MMM yyyy  HH:mm");
        SnapshotKindLabel = snapshot.IsDemo ? "DEMO HISTORY" : string.Empty;
        SelectedJob = FilteredJobs.FirstOrDefault();
        HasNoJobs = Jobs.Count == 0;
    }

    private void RefreshFilteredJobs()
    {
        FilteredJobs.Clear();
        var filter = SelectedLocationFilter;
        foreach (var job in Jobs.Where(job => MatchesLocationFilter(job.Posting, filter)))
        {
            FilteredJobs.Add(job);
        }

        HasNoFilteredJobs = Jobs.Count > 0 && FilteredJobs.Count == 0;
    }

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

    private void SaveStarredState(string jobId, bool isStarred)
    {
        var selectedSnapshotId = SelectedPoint?.Snapshot.Id;
        var selectedJobId = SelectedJob?.Posting.Id;
        _repository.SetStarred(jobId, isStarred);
        PopulateHistory(_repository.GetHistory(), selectedSnapshotId);
        SelectedJob = Jobs.FirstOrDefault(job => string.Equals(
            job.Posting.Id,
            selectedJobId,
            StringComparison.OrdinalIgnoreCase)) ?? Jobs.FirstOrDefault();
    }
}