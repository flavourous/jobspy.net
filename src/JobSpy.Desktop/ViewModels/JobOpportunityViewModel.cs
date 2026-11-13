using System;
using System.Collections.Generic;
using Avalonia.Media;
using JobSpy.Desktop.Models;
using ReactiveUI;
using System.Reactive;

namespace JobSpy.Desktop.ViewModels;

public sealed class JobOpportunityViewModel : ReactiveObject
{
    private static readonly IBrush InferredTypeBrush = new SolidColorBrush(Color.Parse("#52615A"));
    private static readonly IBrush AiCorrectedTypeBrush = new SolidColorBrush(Color.Parse("#D97706"));
    private readonly Action<string, string> _saveStatus;
    private readonly Action<string> _markAvailable;
    private string _selectedStatus;

    public JobOpportunityViewModel(
        JobPosting posting,
        bool isInLatestSnapshot,
        bool needsAvailabilityRepair,
        Action<string, string> saveStatus,
        Action<string> markAvailable)
    {
        Posting = posting;
        IsInLatestSnapshot = isInLatestSnapshot;
        NeedsAvailabilityRepair = needsAvailabilityRepair;
        _saveStatus = saveStatus;
        _markAvailable = markAvailable;
        _selectedStatus = OpportunityStatus.Normalize(posting.Status);
    }

    public JobPosting Posting { get; }

    public string Title => Posting.Title ?? "Untitled role";

    public string Company => Posting.Company ?? "Company not listed";

    public string Location => string.IsNullOrWhiteSpace(Posting.EffectiveLocation)
        ? "Location not listed"
        : Posting.EffectiveLocation;

    public string SalaryLabel => Posting.SalaryLabel;

    public string EmploymentTypeLabel => string.IsNullOrWhiteSpace(Posting.EffectiveJobType)
        ? "Type not listed"
        : Posting.EffectiveJobType;

    public string WorkPatternLabel => Posting.WorkPatternLabel;

    public IBrush WorkPatternForeground => Posting.AiWorkPattern ? AiCorrectedTypeBrush : InferredTypeBrush;

    public IBrush EmploymentTypeForeground => Posting.AiCorrected ? AiCorrectedTypeBrush : InferredTypeBrush;

    public void RefreshEnrichment()
    {
        this.RaisePropertyChanged(nameof(Location));
        this.RaisePropertyChanged(nameof(SalaryLabel));
        this.RaisePropertyChanged(nameof(EmploymentTypeLabel));
        this.RaisePropertyChanged(nameof(EmploymentTypeForeground));
        this.RaisePropertyChanged(nameof(WorkPatternLabel));
        this.RaisePropertyChanged(nameof(WorkPatternForeground));
    }

    public string DatePostedLabel => Posting.DatePostedLabel;

    public string TrackedSinceLabel => Posting.FirstSeenUtc == default
        ? "Tracked date unavailable"
        : $"Tracked since {Posting.FirstSeenUtc.ToLocalTime():dd MMM yyyy}";

    public string LastSeenLabel => Posting.LastSeenUtc == default
        ? "Last seen date unavailable"
        : $"Last seen {Posting.LastSeenUtc.ToLocalTime():dd MMM yyyy}";

    public string AvailabilityLabel => IsInLatestSnapshot
        ? "Available in latest scan"
        : Posting.DisappearedAtUtc.HasValue
            ? $"Disappeared {Posting.DisappearedAtUtc.Value.ToLocalTime():dd MMM yyyy}"
            : "Not found in latest scan";

    public bool IsInLatestSnapshot { get; }

    public bool NeedsAvailabilityRepair { get; }

    public void MarkAvailable() => _markAvailable(Posting.Id);

    public string Description => string.IsNullOrWhiteSpace(Posting.Description)
        ? "No description was provided by the source."
        : Posting.Description;

    public string SiteLabel => string.IsNullOrWhiteSpace(Posting.Site) ? "Job board" : Posting.Site;

    public IReadOnlyList<string> AvailableStatuses => OpportunityStatus.All;

    public string StatusLabel => _selectedStatus.Length == 0
        ? string.Empty
        : char.ToUpperInvariant(_selectedStatus[0]) + _selectedStatus[1..];

    public string SelectedStatus
    {
        get => _selectedStatus;
        set
        {
            var normalizedStatus = OpportunityStatus.Normalize(value);
            if (_selectedStatus == normalizedStatus)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedStatus, normalizedStatus);
            Posting.Status = normalizedStatus;
            this.RaisePropertyChanged(nameof(StatusLabel));
            _saveStatus(Posting.Id, normalizedStatus);
        }
    }

    internal bool ApplyStatusWithoutSaving(string status)
    {
        var normalizedStatus = OpportunityStatus.Normalize(status);
        if (_selectedStatus == normalizedStatus)
        {
            return false;
        }

        _selectedStatus = normalizedStatus;
        Posting.Status = normalizedStatus;
        this.RaisePropertyChanged(nameof(SelectedStatus));
        this.RaisePropertyChanged(nameof(StatusLabel));
        return true;
    }
}
