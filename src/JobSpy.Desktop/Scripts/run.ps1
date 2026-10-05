$ErrorActionPreference = "Stop"
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
dotnet run --project (Join-Path $Root "src\JobSpy.Desktop\JobSpy.Desktop.csproj") @args
exit $LASTEXITCODE