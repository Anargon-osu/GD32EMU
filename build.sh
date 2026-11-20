#!/bin/sh
# Linux / macOS: builds one self-contained ./gd32emu executable, next to this file (needs the .NET 10 SDK).
set -e
cd "$(dirname "$0")"
if ! command -v dotnet >/dev/null 2>&1; then
  echo "Building from source needs the .NET 10 SDK. Download a prebuilt package to run without installing the SDK."
  exit 1
fi
case "$(uname -s)-$(uname -m)" in
  Linux-x86_64) RID=linux-x64 ;; Linux-aarch64) RID=linux-arm64 ;;
  Darwin-x86_64) RID=osx-x64 ;; Darwin-arm64) RID=osx-arm64 ;;
  *) echo "Unsupported platform: $(uname -s) $(uname -m). Supported: Linux x64/arm64, macOS Intel/Apple Silicon."; exit 1 ;;
esac
dotnet publish gd32emu.csproj -c Release -r "$RID" -o .publish
cp .publish/gd32emu ./gd32emu
./gd32emu --list-devices
dotnet run --project tests/EmulatorTests.csproj -c Release
echo "built ./gd32emu"
