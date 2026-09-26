#!/bin/zsh
# Сборка Clipvey.exe для Windows 10/11 x64 прямо на Mac: один файл со встроенной средой .NET.
set -euo pipefail
cd "${0:A:h}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet publish Clipvey.Windows/Clipvey.Windows.csproj -c Release -o ../dist/windows -p:DebugType=none
rm -f ../dist/windows/*.pdb
echo "Готово: ${0:A:h:h}/dist/windows/Clipvey.exe"
