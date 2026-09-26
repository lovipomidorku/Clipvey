#!/bin/zsh
# Сборка Clipvey.exe для Windows 10/11 x64 прямо на Mac: один файл со встроенной средой .NET.
set -euo pipefail
DIR="${0:A:h}"
cd "$DIR"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet publish Clipvey.Windows/Clipvey.Windows.csproj -c Release -o ../dist/windows -p:DebugType=none
rm -f ../dist/windows/*.pdb(N)
echo "Готово: ${DIR:h}/dist/windows/Clipvey.exe"
