using System;
using JobSpy.Desktop.Models;
using ReactiveUI;
using System.Reactive;

namespace JobSpy.Desktop.ViewModels;

public sealed class JobOpportunityViewModel : ReactiveObject
{
    private readonly Action<string, bool> _saveStar;
    private bool _isStarred;

    public JobOpportunityViewModel(JobPosting posting, Action<string, bool> saveStar)
    {
        Posting = posting;
        _saveStar = saveStar;
        _isStarred = posting.IsStarred;
        ToggleStarCommand = ReactiveCommand.Create(() =>
        {
            IsStarred = !IsStarred;
            _saveStar(Posting.Id, IsStarred);
            return Unit.Default;
        });
    }

    public JobPosting Posting { get; }

    public string Title => Posting.Title ?? "Untitled role";

    public string Company => Posting.Company ?? "Company not listed";

    public string Location => Posting.Location ?? "Location not listed";

    public string SalaryLabel => Posting.SalaryLabel;

    public string DatePostedLabel => Posting.DatePostedLabel;

    public string TrackedSinceLabel => Posting.FirstSeenUtc == default
        ? "Tracked date unavailable"
        : $"Tracked since {Posting.FirstSeenUtc.ToLocalTime():dd MMM yyyy}";

    public string LastSeenLabel => Posting.LastSeenUtc == default
        ? "Last seen date unavailable"
        : $"Last seen {Posting.LastSeenUtc.ToLocalTime():dd MMM yyyy}";

    public string AvailabilityLabel => Posting.DisappearedAtUtc.HasValue
        ? $"Disappeared {Posting.DisappearedAtUtc.Value.ToLocalTime():dd MMM yyyy}"
        : "Available in latest scan";

    public string Description => string.IsNullOrWhiteSpace(Posting.Description)
        ? "No description was provided by the source."
        : Posting.Description;

    public string StarGlyph => IsStarred ? "★" : "☆";

    public string SiteLabel => string.IsNullOrWhiteSpace(Posting.Site) ? "Job board" : Posting.Site;

    public ReactiveCommand<ReactiveUI.Primitives.RxVoid, Unit> ToggleStarCommand { get; }

    public bool IsStarred
    {
        get => _isStarred;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isStarred, value);
            this.RaisePropertyChanged(nameof(StarGlyph));
        }
    }
}