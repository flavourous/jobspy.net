using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using JobSpy.Desktop.ViewModels;
using System.Linq;
using System.Windows.Input;

namespace JobSpy.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnJobRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (JobsList is not { } jobsList
            || sender is not Control { DataContext: JobOpportunityViewModel job } row
            || !e.GetCurrentPoint(row).Properties.IsRightButtonPressed
            || jobsList.SelectedItems is not { } selectedItems)
        {
            return;
        }

        e.Handled = true;
        if (!selectedItems.Contains(job))
        {
            selectedItems.Clear();
            jobsList.SelectedItem = job;
        }

        var contextMenu = row.ContextMenu;
        if (contextMenu is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            contextMenu.PlacementTarget = row;
            contextMenu.Placement = PlacementMode.Pointer;
            contextMenu.Open(row);
        });
    }

    private void OnSetSelectedJobsStatus(object? sender, RoutedEventArgs e)
    {
        if (JobsList is not { SelectedItems: { } selectedItems }
            || sender is not MenuItem { Tag: string status }
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var jobIds = selectedItems
            .OfType<JobOpportunityViewModel>()
            .Select(job => job.Posting.Id)
            .ToArray();
        if (jobIds.Length == 0)
        {
            return;
        }

        ((ICommand)viewModel.SetSelectedJobsStatusCommand).Execute(
            new JobStatusChangeRequest(status, jobIds));
    }
}