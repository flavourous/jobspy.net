using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using JobSpy.Desktop.Repositories;
using JobSpy.Desktop.Services;
using JobSpy.Desktop.ViewModels;
using JobSpy.Desktop.Views;
using System;
using System.IO;
using System.Threading.Tasks;
using XamlMcp.Avalonia;

namespace JobSpy.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var startupWindow = new StartupWindow();
            desktop.MainWindow = startupWindow;
            startupWindow.Opened += async (_, _) => await StartApplicationAsync(desktop, startupWindow);
        }

        this.AttachXamlMcp();
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StartApplicationAsync(IClassicDesktopStyleApplicationLifetime desktop, StartupWindow startupWindow)
    {
        try
        {
            await Task.Run(() => Program.ConfigurePythonEnvironment(
                message => Dispatcher.UIThread.Post(() => startupWindow.SetStatus(message))));
            var dataDirectory = JobSpyDataDirectory.GetPath();
            var repository = new LiteDbJobRepository(Path.Combine(dataDirectory, "jobs.db"));
            DemoHistorySeeder.SeedIfRequested(repository);
            var searchService = new PythonJobSpyService();
            var mainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(repository, searchService),
            };
            desktop.Exit += (_, _) => repository.Dispose();
            desktop.MainWindow = mainWindow;
            mainWindow.Show();
            startupWindow.Close();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Unable to prepare JobSpy's Python environment: {exception.Message}");
            startupWindow.SetStatus($"Could not finish setup.\n\n{exception.Message}");
        }
    }

}
