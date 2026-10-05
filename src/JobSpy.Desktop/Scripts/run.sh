#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
exec dotnet run --project "$root/src/JobSpy.Desktop/JobSpy.Desktop.csproj" "$@"