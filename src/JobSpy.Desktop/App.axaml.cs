using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using JobSpy.Desktop.Repositories;
using JobSpy.Desktop.Services;
using JobSpy.Desktop.ViewModels;
using JobSpy.Desktop.Views;
using System;
using System.IO;
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
            var dataDirectory = JobSpyDataDirectory.GetPath();

            var repository = new LiteDbJobRepository(Path.Combine(dataDirectory, "jobs.db"));
            DemoHistorySeeder.SeedIfRequested(repository);
            var searchService = new PythonJobSpyService();
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(repository, searchService),
            };
            desktop.Exit += (_, _) => repository.Dispose();
        }

        this.AttachXamlMcp();
        base.OnFrameworkInitializationCompleted();
    }

}