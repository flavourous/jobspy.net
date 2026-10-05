using Avalonia;
using ReactiveUI.Avalonia;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using JobSpy.Desktop.Services;

namespace JobSpy.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            ConfigurePythonEnvironment();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Unable to prepare JobSpy's Python environment: {exception.Message}");
            return 1;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void ConfigurePythonEnvironment()
    {
        var jobSpyRoot = FindJobSpyRoot();
        var dataRoot = JobSpyDataDirectory.GetPath();

        var sourcePython = FindPythonCommand();
        var virtualEnvironment = Path.Combine(dataRoot, ".venv");
        var venvPython = Path.Combine(virtualEnvironment, OperatingSystem.IsWindows()
            ? Path.Combine("Scripts", "python.exe")
            : Path.Combine("bin", "python"));
        var projectMetadata = Path.Combine(jobSpyRoot, "pyproject.toml");
        var fingerprintPath = Path.Combine(dataRoot, "jobspy-dependencies.sha256");
        var fingerprint = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(projectMetadata)));
        var dependenciesNeedInstall = !File.Exists(venvPython)
            || !File.Exists(fingerprintPath)
            || File.ReadAllText(fingerprintPath) != fingerprint;

        if (!File.Exists(venvPython))
        {
            Console.WriteLine("Creating the JobSpy Python virtual environment...");
            RunProcess(sourcePython, new[] { "-m", "venv", virtualEnvironment }, jobSpyRoot);
        }

        if (dependenciesNeedInstall)
        {
            Console.WriteLine("Installing the local JobSpy checkout and its dependencies...");
            RunProcess(venvPython, new[] { "-m", "pip", "install", "--disable-pip-version-check", "-e", jobSpyRoot }, jobSpyRoot);
            File.WriteAllText(fingerprintPath, fingerprint);
        }

        var pythonInfoJson = RunProcess(venvPython, new[]
        {
            "-c",
            "import glob,json,os,sysconfig; d=sysconfig.get_config_var('LIBDIR') or sysconfig.get_config_var('BINDIR'); n=sysconfig.get_config_var('INSTSONAME') or sysconfig.get_config_var('LDLIBRARY'); p=os.path.join(d,n) if d and n else ''; matches=[p] if p and os.path.isfile(p) else glob.glob(os.path.join(d or '', 'libpython*.so*'))+glob.glob(os.path.join(d or '', 'libpython*.dylib'))+glob.glob(os.path.join(d or '', 'python*.dll')); print(json.dumps({'dll': matches[0] if matches else '', 'site_packages': sysconfig.get_paths()['purelib']}))",
        }, jobSpyRoot);
        using var pythonInfo = JsonDocument.Parse(pythonInfoJson);
        var pythonDll = pythonInfo.RootElement.GetProperty("dll").GetString();
        if (string.IsNullOrWhiteSpace(pythonDll) || !File.Exists(pythonDll))
        {
            throw new FileNotFoundException("Could not locate Python's shared library. Install Python with its shared library development package.");
        }

        var sitePackages = pythonInfo.RootElement.GetProperty("site_packages").GetString();
        var importPaths = new[] { jobSpyRoot, sitePackages }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Append(Environment.GetEnvironmentVariable("PYTHONPATH"))
            .Where(path => !string.IsNullOrWhiteSpace(path));
        Environment.SetEnvironmentVariable("PYTHONNET_PYDLL", pythonDll);
        Environment.SetEnvironmentVariable(
            "PYTHONPATH",
            string.Join(Path.PathSeparator, importPaths));
    }

    private static string FindJobSpyRoot()
    {
        var configuredPath = Environment.GetEnvironmentVariable("JOBSPY_SOURCE_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var configuredRoot = Path.GetFullPath(configuredPath);
            if (File.Exists(Path.Combine(configuredRoot, "pyproject.toml")))
            {
                return configuredRoot;
            }

            throw new DirectoryNotFoundException($"JOBSPY_SOURCE_PATH does not contain JobSpy: {configuredRoot}");
        }

        foreach (var startPath in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(startPath);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "vendor", "JobSpy");
                if (File.Exists(Path.Combine(candidate, "pyproject.toml")))
                {
                    return candidate;
                }
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "The vendor/JobSpy checkout was not found. Initialize the Git submodule or set JOBSPY_SOURCE_PATH.");
    }

    private static string FindPythonCommand()
    {
        var configuredCommand = Environment.GetEnvironmentVariable("PYTHON");
        var candidates = string.IsNullOrWhiteSpace(configuredCommand)
            ? OperatingSystem.IsWindows() ? new[] { "python", "python3" } : new[] { "python3", "python" }
            : new[] { configuredCommand };

        foreach (var candidate in candidates)
        {
            try
            {
                RunProcess(candidate, new[] { "-c", "import sys; assert sys.version_info >= (3, 10), 'Python 3.10 or newer is required'" }, Environment.CurrentDirectory);
                return candidate;
            }
            catch (Exception) when (string.IsNullOrWhiteSpace(configuredCommand))
            {
            }
        }

        throw new InvalidOperationException("Python 3.10 or newer was not found. Install Python or set the PYTHON environment variable.");
    }

    private static string RunProcess(string executable, IEnumerable<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start process: {executable}");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(standardOutput, standardError);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(executable)} exited with code {process.ExitCode}: {standardError.Result.Trim()}");
        }

        return standardOutput.Result.Trim();
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseReactiveUI(_ => { })
            .WithInterFont()
            .LogToTrace();
}
