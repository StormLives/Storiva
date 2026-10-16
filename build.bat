@echo off
rem ---------------------------------------------------------------------------------------------
rem  Storiva - build your own Storiva.exe from this source code.
rem
rem  Result: dist\Storiva.exe, a single portable file for 64-bit Windows 10/11 that already
rem  includes the .NET runtime (nothing else to install to run it).
rem
rem  Requirement to BUILD: the .NET 8 SDK, version 8.0.426 or newer in the 8.0 band
rem  (https://dotnet.microsoft.com/download/dotnet/8.0).
rem
rem  Usage:   build.bat              build dist\Storiva.exe
rem           build.bat test         run the unit tests first, then build
rem           add "nopause" to skip the final "Press any key" (e.g. build.bat test nopause)
rem ---------------------------------------------------------------------------------------------
setlocal EnableExtensions
cd /d "%~dp0"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
set "ERR=0"
set "RUNTESTS="
set "NOPAUSE="
for %%A in (%*) do (
  if /i "%%~A"=="test" set "RUNTESTS=1"
  if /i "%%~A"=="nopause" set "NOPAUSE=1"
)
if defined CI set "NOPAUSE=1"

rem Find a dotnet that has a compatible SDK (global.json picks the version): PATH first, then the
rem per-user and machine-wide install folders.
set "DOTNET="
for /f "delims=" %%D in ('where dotnet 2^>nul') do call :trydotnet "%%D"
call :trydotnet "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
call :trydotnet "%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET goto :nosdk
for /f "delims=" %%V in ('call "%DOTNET%" --version') do set "SDKVERSION=%%V"
echo Using .NET SDK %SDKVERSION% (%DOTNET%)
echo.

if defined RUNTESTS (
  echo Running the unit tests...
  "%DOTNET%" test Storiva.sln -c Release --nologo -v q || goto :failed
  echo.
)

echo Building dist\Storiva.exe ...
if exist dist rmdir /s /q dist
"%DOTNET%" publish src\Storiva.App\Storiva.App.csproj -c Release -p:PublishProfile=win-x64-portable -p:DebugType=none -o dist --nologo -v q || goto :failed

echo.
echo Done: %CD%\dist\Storiva.exe
echo If you share it, keep LICENSE.txt, THIRD_PARTY_NOTICES.md and the licenses folder with it.
goto :end

:nosdk
echo.
echo No compatible .NET 8 SDK (8.0.426 or newer) was found.
echo Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and run build.bat again.
set "ERR=1"
goto :end

:failed
echo.
echo Build failed. See the messages above.
set "ERR=1"

:end
if not defined NOPAUSE pause
endlocal & exit /b %ERR%

:trydotnet
if defined DOTNET exit /b 0
if not exist "%~1" exit /b 0
"%~1" --version >nul 2>&1 && set "DOTNET=%~1"
exit /b 0
