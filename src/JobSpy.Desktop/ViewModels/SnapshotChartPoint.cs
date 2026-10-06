using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using JobSpy.Desktop.Models;
using ReactiveUI;
using System.Reactive;

namespace JobSpy.Desktop.ViewModels;

public sealed class SnapshotChartPoint : ReactiveObject
{
    private bool _isSelected;

    public SnapshotChartPoint(JobSearchSnapshot snapshot, IReadOnlyList<SalaryChartSegment> segments, Action<SnapshotChartPoint> select)
    {
        Snapshot = snapshot;
        UpdateSegments(segments);
        SelectCommand = ReactiveCommand.Create(() =>
        {
            select(this);
            return Unit.Default;
        });
    }

    public JobSearchSnapshot Snapshot { get; }

    public IReadOnlyList<SalaryChartSegment> Segments { get; private set; } = Array.Empty<SalaryChartSegment>();

    public decimal? MedianSalaryGbp { get; private set; }

    public string DateLabel => Snapshot.CapturedAtUtc.ToLocalTime().ToString("dd MMM");

    public string TimeLabel => Snapshot.CapturedAtUtc.ToLocalTime().ToString("HH:mm");

    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    public void UpdateSegments(IReadOnlyList<SalaryChartSegment> segments)
    {
        Segments = segments;
        MedianSalaryGbp = Median(segments.Select(segment => segment.SalaryGbp).OrderBy(salary => salary).ToArray());
        this.RaisePropertyChanged(nameof(Segments));
        this.RaisePropertyChanged(nameof(MedianSalaryGbp));
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
}

public sealed class SalaryChartSegment
{
    public SalaryChartSegment(JobPosting posting, decimal salaryGbp, bool isEstimated, bool isManagement, bool needsRelocation)
    {
        Posting = posting;
        SalaryGbp = salaryGbp;
        IsEstimated = isEstimated;
        IsManagement = isManagement;
        NeedsRelocation = needsRelocation;
    }

    public JobPosting Posting { get; }

    public decimal SalaryGbp { get; }

    public bool IsEstimated { get; }

    public bool IsManagement { get; }

    public bool NeedsRelocation { get; }
}

public sealed record SalaryChartRunSelection(SnapshotChartPoint Point, string JobId);