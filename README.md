# JobSpy Desktop

A cross-platform Avalonia desktop client for tracking senior engineering opportunities over time with the locally checked-out [JobSpy](https://github.com/speedyapply/JobSpy) project.

## Requirements

- .NET 10 SDK
- Python 3.10 or newer, including its shared library
- Git

## Setup and run

```bash
git submodule update --init --recursive
dotnet run --project src/JobSpy.Desktop/JobSpy.Desktop.csproj
```

On first launch, the app creates a virtual environment in the user's local application data directory, installs the editable JobSpy submodule and dependencies, and configures Python.NET. Python 3.10 or newer, including its shared library, must be installed. Set `PYTHON` to choose a Python executable or `JOBSPY_SOURCE_PATH` to use a different local JobSpy checkout.

The database is created at the operating system's local application data directory under `JobSpy/jobs.db`; set `JOBSPY_DATA_DIRECTORY` to use a different data directory. Each profile scan records a dated snapshot of available postings and observed salaries. Postings retain their first-seen, last-seen, and disappearance dates, and starred roles remain saved across scans. The history chart shows role counts and median annual GBP salary reported by supported listings; salary comparisons include only explicit GBP values with a recognized pay interval.

The built-in profile searches for lead .NET, principal C#, staff backend, distributed-systems, and software engineering management roles across the United Kingdom. Select the job boards to include, then run the profile regularly to build a useful history; scans are manual and are not scheduled automatically.

Debug builds include [HotAvalonia](https://github.com/Kira-NT/HotAvalonia) for live `.axaml` reload. Launch the app as usual and save a XAML file to apply the changes to the running window.

To populate an empty database with clearly labeled synthetic history for UI checks, set `JOBSPY_DEMO_DATA=1` before launching. The sample seed runs only when the selected database has no scan history.

## UI inspection with XamlMcp

The Debug build attaches the XamlMcp Avalonia agent automatically. In VS Code, start the `xamlmcp` MCP server from `.vscode/mcp.json`, then run the app with `dotnet run --project src/JobSpy.Desktop/JobSpy.Desktop.csproj`. The running app will be available to XamlMcp for UI inspection and interaction. The agent is not attached to Release builds.

## Project layout

- `src/JobSpy.Desktop` contains the Avalonia, ReactiveUI, Python.NET service, and repository code.
- `vendor/JobSpy` is the upstream JobSpy Git submodule and can be edited locally.

JobSpy makes requests to third-party job boards. Follow each site's terms, rate limits, and applicable laws when searching; results depend on the upstream sources and may be incomplete or unavailable.