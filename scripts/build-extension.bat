@echo off
REM Build the PlotterHelper engine as a single-file binary and assemble the
REM Inkscape extension folder (PlotterHelper + plotter-helper.py + plotter-helper.inx).
setlocal enabledelayedexpansion

REM Resolve repo root = parent of this script's directory (scripts/)
for %%I in ("%~dp0..") do set "ROOT=%%~fI"
set "OUT=%ROOT%\dist\plotter-helper-extension"

REM Defaults - override with env vars
if not defined RID set "RID=win-x64"
if not defined SELF_CONTAINED set "SELF_CONTAINED=false"
if not defined AUTO_INSTALL set "AUTO_INSTALL=false"

echo ^>^> Publishing PlotterHelper for %RID% (self-contained=%SELF_CONTAINED%)

set "SC_FLAG="
if /i "%SELF_CONTAINED%"=="true" set "SC_FLAG=--self-contained true"

REM Temp publish dir
set "PUB=%TEMP%\plothelper_%RANDOM%%RANDOM%"

dotnet publish "%ROOT%\src\PlotterHelper.Cli\PlotterHelper.Cli.csproj" -c Release -r "%RID%" -o "%PUB%" %SC_FLAG% -p:InkscapeAutoInstall=%AUTO_INSTALL%
if errorlevel 1 (
    echo ERROR: dotnet publish failed
    rmdir /s /q "%PUB%" 2>nul
    exit /b 1
)

echo ^>^> Assembling extension in %OUT%
if exist "%OUT%" rmdir /s /q "%OUT%"
mkdir "%OUT%" 2>nul

copy /y "%PUB%\PlotterHelper.exe"       "%OUT%\PlotterHelper.exe"        >nul
copy /y "%ROOT%\inkscape\plotter-helper.py"  "%OUT%\plotter-helper.py"   >nul
copy /y "%ROOT%\inkscape\plotter-helper.inx" "%OUT%\plotter-helper.inx"  >nul
copy /y "%ROOT%\inkscape\plotter-helper-icon.svg" "%OUT%\plotter-helper-icon.svg" >nul

rmdir /s /q "%PUB%" 2>nul

echo ^>^> Done. Extension assembled in %OUT%
echo     Copy it into Inkscape's user extensions dir, or rerun with AUTO_INSTALL=true.
dir "%OUT%"

endlocal
pause