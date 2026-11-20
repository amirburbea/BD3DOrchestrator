@echo off
rem Updates the local copy of BD3DOrchestrator from the NAS, then starts it.
rem Copy THIS file to each PC once (or make a shortcut to a local copy). It lives outside the mirrored folder on purpose,
rem because a batch file must not be overwritten while it is running.
rem
rem Per-machine settings go in appsettings.local.json next to the installed exe; updates never touch that file.
rem Overrides for testing:  set ORCH_SOURCE=...   set ORCH_DEST=...
rem (Deliberately NOT named BD3D_*: the app reads every BD3D_* environment variable as a setting and rejects unknown ones.)
setlocal

if not defined ORCH_SOURCE set "ORCH_SOURCE=\\192.168.253.3\Software\BD3DOrchestrator"
if not defined ORCH_DEST set "ORCH_DEST=%LOCALAPPDATA%\BD3DOrchestrator"

rem Any number of copies may run at once. The update is skipped while one is running, because its files are in use.
set "RUNNING=0"
tasklist /FI "IMAGENAME eq BD3DOrchestrator.exe" 2>nul | find /I "BD3DOrchestrator.exe" >nul
if not errorlevel 1 set "RUNNING=1"

set "UPDATED=0"
rem Only mirror when the source really holds a build, so a missing or empty share can never wipe the local install.
if "%RUNNING%"=="1" (
    echo Another copy is running, so the update was skipped. Close every copy and launch again to update.
) else if exist "%ORCH_SOURCE%\BD3DOrchestrator.exe" (
    echo Updating from %ORCH_SOURCE% ...
    robocopy "%ORCH_SOURCE%" "%ORCH_DEST%" /MIR /R:2 /W:2 /NP /NFL /NDL /NJH /XF appsettings.local.json crash.log Launch-BD3DOrchestrator.cmd
    if errorlevel 8 (
        echo WARNING: the update failed.
    ) else (
        set "UPDATED=1"
        rem Clear the "downloaded from another computer" block (Zone.Identifier) Windows may have put on the copied files.
        powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -LiteralPath '%ORCH_DEST%' -Recurse -File | Unblock-File"
        rem First run on this PC: create its own settings file from the template. It is never overwritten after that.
        if not exist "%ORCH_DEST%\appsettings.local.json" if exist "%ORCH_DEST%\appsettings.local.template.json" (
            copy "%ORCH_DEST%\appsettings.local.template.json" "%ORCH_DEST%\appsettings.local.json" >nul
            echo.
            echo First run on this PC: created %ORCH_DEST%\appsettings.local.json from the template.
            echo The shared settings already apply. Edit that file only if this PC needs different paths.
            pause
        )
    )
) else (
    echo WARNING: cannot find %ORCH_SOURCE%\BD3DOrchestrator.exe
)

if not exist "%ORCH_DEST%\BD3DOrchestrator.exe" (
    echo ERROR: nothing is installed yet and the NAS build is not reachable.
    pause
    exit /b 1
)

if "%UPDATED%"=="0" if "%RUNNING%"=="0" (
    echo Starting the last installed version.
    rem (ping is a 5-second delay that also works when stdin is redirected, unlike timeout)
    ping -n 6 127.0.0.1 >nul
)

start "" "%ORCH_DEST%\BD3DOrchestrator.exe"
exit /b 0
