@echo off
rem Builds gd32emu.exe, next to this file, with the C# compiler that ships with Windows (.NET Framework 4).
setlocal EnableExtensions DisableDelayedExpansion
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo The C# compiler was not found at "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
  exit /b 1
)
"%CSC%" /nologo /langversion:5 /optimize+ /out:gd32emu.exe gd32emu.cs gd32emu_cli.cs
if errorlevel 1 exit /b 1
gd32emu.exe --list-devices
if errorlevel 1 exit /b 1
if not exist tests\work mkdir tests\work
"%CSC%" /nologo /langversion:5 /optimize+ /main:Gd32Emu.Tests.EmulatorTests /out:tests\work\EmulatorTests.exe gd32emu.cs gd32emu_cli.cs tests\EmulatorTests.cs
if errorlevel 1 exit /b 1
tests\work\EmulatorTests.exe
exit /b %errorlevel%
