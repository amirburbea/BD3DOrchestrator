@echo off
rem Builds a fresh Release of BD3DOrchestrator and mirrors it to the NAS, where every PC's launcher pulls it from.
rem Override the target for testing:  set ORCH_SOURCE=C:\some\test\folder
setlocal

if not defined ORCH_SOURCE set "ORCH_SOURCE=\\192.168.253.3\Software\BD3DOrchestrator"
set "STAGE=%TEMP%\BD3DOrchestrator-publish"

if exist "%STAGE%" rmdir /s /q "%STAGE%"
dotnet publish "%~dp0..\BD3DOrchestrator\BD3DOrchestrator.csproj" -c Release --no-self-contained -o "%STAGE%"
if errorlevel 1 (
    echo.
    echo Build failed - nothing was published.
    exit /b 1
)

copy /y "%~dp0Launch-BD3DOrchestrator.cmd" "%STAGE%\" >nul
copy /y "%~dp0appsettings.local.template.json" "%STAGE%\" >nul

rem /MIR makes the NAS folder an exact copy of the build (it removes stale files there).
robocopy "%STAGE%" "%ORCH_SOURCE%" /MIR /R:2 /W:2 /NP
if errorlevel 8 (
    echo.
    echo Publish to %ORCH_SOURCE% FAILED.
    exit /b 1
)

echo.
echo Published to %ORCH_SOURCE%
exit /b 0
