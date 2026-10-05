using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using JobSpy.Desktop.ViewModels;

namespace JobSpy.Desktop.Controls;

public sealed class SalaryHistoryChart : Control
{
    private const double ColumnSpacing = 112;
    private const double BarWidth = 38;
    private const double PlotTop = 30;
    private const double PlotBottom = 184;
    private const double SidePadding = 64;
    private const double RowGap = 2;
    private const double HoverOpacity = 0.35;
    private static readonly Color PermanentColor = Color.Parse("#168264");
    private static readonly Color ContractColor = Color.Parse("#C36A3D");
    private static readonly Color PartTimeColor = Color.Parse("#397AA6");
    private static readonly Color OtherColor = Color.Parse("#78847D");
    private static readonly Color GridColor = Color.Parse("#E4EAE5");
    private static readonly Color TextColor = Color.Parse("#52615A");
    private static readonly Color ManagementOverlayColor = Color.Parse("#1A7030A0");
    private IReadOnlyList<Dictionary<string, SegmentSlot>> _renderedSlotMaps = Array.Empty<Dictionary<string, SegmentSlot>>();
    private string? _hoveredJobId;

    public static readonly StyledProperty<ObservableCollection<SnapshotChartPoint>?> ItemsSourceProperty =
        AvaloniaProperty.Register<SalaryHistoryChart, ObservableCollection<SnapshotChartPoint>?>(nameof(ItemsSource));

    public static readonly StyledProperty<SnapshotChartPoint?> SelectedPointProperty =
        AvaloniaProperty.Register<SalaryHistoryChart, SnapshotChartPoint?>(nameof(SelectedPoint));

    public static readonly StyledProperty<ICommand?> SelectionCommandProperty =
        AvaloniaProperty.Register<SalaryHistoryChart, ICommand?>(nameof(SelectionCommand));

    public static readonly StyledProperty<ICommand?> RunSelectionCommandProperty =
        AvaloniaProperty.Register<SalaryHistoryChart, ICommand?>(nameof(RunSelectionCommand));

    static SalaryHistoryChart()
    {
        AffectsRender<SalaryHistoryChart>(ItemsSourceProperty, SelectedPointProperty);
        AffectsMeasure<SalaryHistoryChart>(ItemsSourceProperty);
    }

    public ObservableCollection<SnapshotChartPoint>? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public SnapshotChartPoint? SelectedPoint
    {
        get => GetValue(SelectedPointProperty);
        set => SetValue(SelectedPointProperty, value);
    }

    public ICommand? SelectionCommand
    {
        get => GetValue(SelectionCommandProperty);
        set => SetValue(SelectionCommandProperty, value);
    }

    public ICommand? RunSelectionCommand
    {
        get => GetValue(RunSelectionCommandProperty);
        set => SetValue(RunSelectionCommandProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = Math.Max(240, (ItemsSource?.Count ?? 0) * ColumnSpacing + SidePadding);
        return new Size(width, 230);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty)
        {
            if (change.OldValue is ObservableCollection<SnapshotChartPoint> previous)
            {
                previous.CollectionChanged -= OnItemsChanged;
            }

            if (change.NewValue is ObservableCollection<SnapshotChartPoint> current)
            {
                current.CollectionChanged += OnItemsChanged;
            }

            InvalidateMeasure();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var points = ItemsSource;
        if (points is null || points.Count == 0)
        {
            return;
        }

        var maxTotal = points
            .Select(point => point.Segments.Sum(segment => segment.SalaryGbp))
            .DefaultIfEmpty(0)
            .Max();
        var plotHeight = PlotBottom - PlotTop;
        var maxSegmentCount = points.Max(point => point.Segments.Count);
        var rowGap = maxSegmentCount > 1
            ? Math.Min(RowGap, plotHeight * 0.2 / (maxSegmentCount - 1))
            : 0;
        var maxValue = Math.Max(1, (double)maxTotal);
        var salaryScale = points
            .Where(point => point.Segments.Count > 0)
            .Select(point =>
            {
                var total = (double)point.Segments.Sum(segment => segment.SalaryGbp);
                var gaps = Math.Max(0, point.Segments.Count - 1) * rowGap;
                return total > 0 ? (plotHeight - gaps) / total : 0;
            })
            .Where(scale => scale > 0)
            .DefaultIfEmpty(0)
            .Min();
        var slotMaps = new List<Dictionary<string, SegmentSlot>>(points.Count);

        for (var index = 0; index < points.Count; index++)
        {
            var x = SidePadding + index * ColumnSpacing;
            var slots = new Dictionary<string, SegmentSlot>(StringComparer.Ordinal);
            var y = PlotBottom;
            for (var segmentIndex = 0; segmentIndex < points[index].Segments.Count; segmentIndex++)
            {
                var segment = points[index].Segments[segmentIndex];
                var height = salaryScale * (double)segment.SalaryGbp;
                slots[segment.Posting.Id] = new SegmentSlot(segment, x, y - height, y, height);
                y -= height;
                if (segmentIndex < points[index].Segments.Count - 1)
                {
                    y -= rowGap;
                }
            }

            slotMaps.Add(slots);
        }

        _renderedSlotMaps = slotMaps;
        DrawSelectionBands(context, points);
        DrawScale(context, maxValue, salaryScale);
        DrawContinuity(context, points, slotMaps);
        DrawColumns(context, points, slotMaps, salaryScale);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var points = ItemsSource;
        if (points is null || points.Count == 0)
        {
            return;
        }

        var position = e.GetPosition(this);
        var point = FindPointAtX(position.X, points);
        if (point is null)
        {
            return;
        }

        var jobId = FindRunAt(position);
        if (jobId is not null && RunSelectionCommand?.CanExecute(null) == true)
        {
            RunSelectionCommand.Execute(new SalaryChartRunSelection(point, jobId));
            e.Handled = true;
            return;
        }

        if (SelectionCommand?.CanExecute(point) == true)
        {
            SelectionCommand.Execute(point);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var jobId = FindRunAt(e.GetPosition(this));
        if (!string.Equals(jobId, _hoveredJobId, StringComparison.Ordinal))
        {
            _hoveredJobId = jobId;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoveredJobId is not null)
        {
            _hoveredJobId = null;
            InvalidateVisual();
        }
    }

    private SnapshotChartPoint? FindPointAtX(double x, IReadOnlyList<SnapshotChartPoint> points)
    {
        var index = (int)Math.Round((x - SidePadding) / ColumnSpacing);
        if (index < 0 || index >= points.Count || Math.Abs(x - (SidePadding + index * ColumnSpacing)) > ColumnSpacing / 2)
        {
            return null;
        }

        return points[index];
    }

    private string? FindRunAt(Point position)
    {
        var points = ItemsSource;
        if (points is null || _renderedSlotMaps.Count != points.Count)
        {
            return null;
        }

        for (var index = 0; index < _renderedSlotMaps.Count; index++)
        {
            var columnX = SidePadding + index * ColumnSpacing;
            if (Math.Abs(position.X - columnX) > BarWidth / 2)
            {
                continue;
            }

            return _renderedSlotMaps[index].Values
                .FirstOrDefault(slot => position.Y >= slot.Top && position.Y <= slot.Bottom)
                ?.Segment.Posting.Id;
        }

        for (var index = 0; index < _renderedSlotMaps.Count - 1; index++)
        {
            var leftEdge = SidePadding + index * ColumnSpacing + BarWidth / 2;
            var rightEdge = SidePadding + (index + 1) * ColumnSpacing - BarWidth / 2;
            if (position.X < leftEdge || position.X > rightEdge)
            {
                continue;
            }

            var progress = (position.X - leftEdge) / (rightEdge - leftEdge);
            foreach (var (jobId, left) in _renderedSlotMaps[index])
            {
                if (!_renderedSlotMaps[index + 1].TryGetValue(jobId, out var right))
                {
                    continue;
                }

                var top = left.Top + (right.Top - left.Top) * progress;
                var bottom = left.Bottom + (right.Bottom - left.Bottom) * progress;
                if (position.Y >= Math.Min(top, bottom) && position.Y <= Math.Max(top, bottom))
                {
                    return jobId;
                }
            }
        }

        return null;
    }

    private void DrawScale(DrawingContext context, double maxValue, double salaryScale)
    {
        var gridPen = new Pen(new SolidColorBrush(GridColor), 1);
        for (var tick = 0; tick <= 4; tick++)
        {
            var amount = maxValue * tick / 4;
            var y = PlotBottom - salaryScale * amount;
            context.DrawLine(gridPen, new Point(SidePadding - 24, y), new Point(Bounds.Width, y));
            DrawText(context, FormatSalary(amount), new Point(0, y - 7), 9, TextColor);
        }
    }

    private void DrawSelectionBands(DrawingContext context, IReadOnlyList<SnapshotChartPoint> points)
    {
        var selectedIndex = -1;
        for (var index = 0; index < points.Count; index++)
        {
            if (points[index] == SelectedPoint)
            {
                selectedIndex = index;
                break;
            }
        }

        if (selectedIndex < 0)
        {
            return;
        }

        var x = SidePadding + selectedIndex * ColumnSpacing;
        var selection = new Rect(x - ColumnSpacing / 2 + 4, 0, ColumnSpacing - 8, Bounds.Height);
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#F2F6F3")), null, selection);
    }

    private void DrawContinuity(
        DrawingContext context,
        IReadOnlyList<SnapshotChartPoint> points,
        IReadOnlyList<Dictionary<string, SegmentSlot>> slotMaps)
    {
        for (var index = 0; index < points.Count - 1; index++)
        {
            foreach (var (jobId, left) in slotMaps[index])
            {
                if (!slotMaps[index + 1].TryGetValue(jobId, out var right))
                {
                    continue;
                }

                var geometry = new StreamGeometry();
                using (var path = geometry.Open())
                {
                    path.BeginFigure(new Point(left.Right, left.Top), true);
                    path.LineTo(new Point(right.Left, right.Top));
                    path.LineTo(new Point(right.Left, right.Bottom));
                    path.LineTo(new Point(left.Right, left.Bottom));
                    path.EndFigure(true);
                }

                var isHovered = jobId == _hoveredJobId;
                context.DrawGeometry(CreateBrush(left.Segment, isHovered), null, geometry);
                if (left.Segment.IsManagement)
                {
                    context.DrawGeometry(
                        new SolidColorBrush(ManagementOverlayColor, isHovered ? HoverOpacity : 1),
                        null,
                        geometry);
                }

                if (left.Segment.IsEstimated)
                {
                    DrawHatching(context, geometry, new Rect(left.Right, Math.Min(left.Top, right.Top), right.Left - left.Right, Math.Max(left.Bottom, right.Bottom) - Math.Min(left.Top, right.Top)), isHovered ? HoverOpacity : 1);
                }

            }
        }
    }

    private void DrawColumns(
        DrawingContext context,
        IReadOnlyList<SnapshotChartPoint> points,
        IReadOnlyList<Dictionary<string, SegmentSlot>> slotMaps,
        double salaryScale)
    {
        var axisPen = new Pen(new SolidColorBrush(Color.Parse("#BFC9C2")), 1);
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            var x = SidePadding + index * ColumnSpacing;
            var total = point.Segments.Sum(segment => segment.SalaryGbp);
            var totalHeight = salaryScale * (double)total;
            DrawCenteredText(context, $"{point.Segments.Count} roles", x, 2, 9, TextColor);
            DrawCenteredText(context, point.MedianSalaryGbp.HasValue
                ? $"Median {FormatSalary((double)point.MedianSalaryGbp.Value)}"
                : "Median n/a", x, 15, 9, TextColor);

            context.DrawLine(axisPen, new Point(x - BarWidth / 2, PlotBottom), new Point(x + BarWidth / 2, PlotBottom));
            foreach (var slot in slotMaps[index].Values)
            {
                DrawSegment(context, slot, slot.Segment.Posting.Id == _hoveredJobId);
            }

            DrawCenteredText(context, point.DateLabel, x, 190, 10, TextColor);
            DrawCenteredText(context, point.TimeLabel, x, 205, 9, TextColor);
            if (totalHeight == 0)
            {
                DrawCenteredText(context, "no salary data", x, PlotBottom - 16, 8, TextColor);
            }
        }
    }

    private static void DrawSegment(DrawingContext context, SegmentSlot slot, bool isHovered)
    {
        if (slot.Height <= 0)
        {
            return;
        }

        var rect = new Rect(slot.Left, slot.Top, BarWidth, slot.Height);
        context.DrawRectangle(CreateBrush(slot.Segment, isHovered), null, rect);
        if (slot.Segment.IsManagement)
        {
            context.DrawRectangle(
                new SolidColorBrush(ManagementOverlayColor, isHovered ? HoverOpacity : 1),
                null,
                rect);
        }

        if (!slot.Segment.IsEstimated || slot.Height < 3)
        {
            return;
        }

        DrawHatching(context, rect, isHovered ? HoverOpacity : 1);
    }

    private static void DrawHatching(DrawingContext context, StreamGeometry geometry, Rect bounds, double opacity)
    {
        using (context.PushGeometryClip(geometry))
        {
            DrawHatchingLines(context, bounds, opacity);
        }
    }

    private static void DrawHatching(DrawingContext context, Rect bounds, double opacity)
    {
        using (context.PushClip(bounds))
        {
            DrawHatchingLines(context, bounds, opacity);
        }
    }

    private static void DrawHatchingLines(DrawingContext context, Rect bounds, double opacity)
    {
        var hatchPen = new Pen(new SolidColorBrush(Color.Parse("#283A32"), 0.5 * opacity), 0.7);
        for (var offset = -bounds.Height; offset < bounds.Width; offset += 6)
        {
            context.DrawLine(hatchPen,
                new Point(bounds.Left + offset, bounds.Bottom),
                new Point(bounds.Left + offset + bounds.Height, bounds.Top));
        }
    }

    private static SolidColorBrush CreateBrush(SalaryChartSegment segment, bool isHovered)
    {
        var color = GetEmploymentColor(segment.Posting.JobType);
        var opacity = (segment.NeedsRelocation ? 0.45 : 1) * (isHovered ? HoverOpacity : 1);
        return new SolidColorBrush(color, opacity);
    }

    private static Color GetEmploymentColor(string? jobType)
    {
        if (jobType?.Contains("contract", StringComparison.OrdinalIgnoreCase) == true
            || jobType?.Contains("freelance", StringComparison.OrdinalIgnoreCase) == true
            || jobType?.Contains("temporary", StringComparison.OrdinalIgnoreCase) == true)
        {
            return ContractColor;
        }

        if (jobType?.Contains("part", StringComparison.OrdinalIgnoreCase) == true)
        {
            return PartTimeColor;
        }

        if (jobType?.Contains("full", StringComparison.OrdinalIgnoreCase) == true
            || jobType?.Contains("permanent", StringComparison.OrdinalIgnoreCase) == true)
        {
            return PermanentColor;
        }

        return OtherColor;
    }

    private static string FormatSalary(double salary) => salary >= 1000000
        ? $"£{salary / 1000000:N1}m"
        : $"£{salary / 1000:N0}k";

    private static void DrawCenteredText(DrawingContext context, string value, double centerX, double y, double size, Color color)
    {
        var text = CreateText(value, size, color);
        context.DrawText(text, new Point(centerX - text.Width / 2, y));
    }

    private static void DrawText(DrawingContext context, string value, Point origin, double size, Color color)
        => context.DrawText(CreateText(value, size, color), origin);

    private static FormattedText CreateText(string value, double size, Color color)
        => new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Inter"), size, new SolidColorBrush(color));

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    private sealed record SegmentSlot(SalaryChartSegment Segment, double CenterX, double Top, double Bottom, double Height)
    {
        public double Left => CenterX - BarWidth / 2;

        public double Right => CenterX + BarWidth / 2;
    }
}