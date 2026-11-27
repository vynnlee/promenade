#!/bin/bash
# Builds bin/Release/net8.0/Promenade.rhp.
set -euo pipefail
root=$(cd "$(dirname -- "$0")/.." && pwd)
dotnet=$(command -v dotnet || echo "$HOME/.dotnet/dotnet")
"$dotnet" build "$root/Promenade.csproj" -c Release
