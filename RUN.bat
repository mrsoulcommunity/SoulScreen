@echo off
rem Builds SoulScreen and starts it. Double-click it, or run it from a prompt; any
rem arguments go to the app, e.g.  RUN.bat --minimized
rem
rem The build lands in artifacts\run\build and is copied to artifacts\run\app, which is
rem where the app runs from. Nothing ever runs from the build folder, so building never
rem trips over files a running copy holds open, and a build that fails leaves the copy
rem already running untouched.

setlocal EnableExtensions
pushd "%~dp0"

set "PROJECT=src\SoulScreen.App\SoulScreen.App.csproj"
set "APP_EXE=SoulScreen.App.exe"
set "BUILD_DIR=%~dp0artifacts\run\build"
set "APP_DIR=%~dp0artifacts\run\app"

rem ---------------------------------------------------------------- prerequisites

where dotnet >nul 2>&1
if errorlevel 1 (
    echo The .NET 8 SDK is needed to build SoulScreen:
    echo     winget install Microsoft.DotNet.SDK.8
    goto :fail
)

rem Neither native piece stops the build - the app starts without them and says what is
rem missing - but a copy that cannot mirror is worth a word before it opens.
if not exist "native\ffmpeg\avcodec-*.dll" (
    echo warning: FFmpeg is missing, so nothing will be decoded. Fetch it with
    echo          pwsh tools\fetch-ffmpeg.ps1
    echo.
)
if not exist "native\soulscreen_fairplay.dll" (
    echo warning: the FairPlay helper is missing, so wireless mirroring stops at the
    echo          handshake. Build it with  pwsh tools\build-fairplay.ps1
    echo.
)

rem ------------------------------------------------------------------------ build

rem Release, so the managed receive, decrypt and pacing code runs optimised.
echo Building SoulScreen...
dotnet build "%PROJECT%" -c Release -o "%BUILD_DIR%" --nologo -v quiet -clp:NoSummary
if errorlevel 1 (
    echo.
    echo The build failed; the errors are above. Nothing was closed or started.
    goto :fail
)

rem Timestamp-literal fence: catches new ad-hoc date-formatting sites that bypass the
rem CaptureTimestampFormatter / TimestampFormatting helpers. Fails the script with a
rem pointer to the offending file and line.
where powershell >nul 2>&1
if not errorlevel 1 (
    powershell -ExecutionPolicy Bypass -File "%~dp0scripts\check-timestamp-literals.ps1"
    if errorlevel 1 (
        echo.
        echo The build has timestamp-literal sites that bypass the formatter. The CI step
        echo above shows the path:line of each. Route them through the helpers instead.
        goto :fail
    )
)

rem -------------------------------------------------------------------------- run

call :close_running_copy
if errorlevel 2 (
    echo SoulScreen was left running.
    goto :done
)
if errorlevel 1 (
    echo Could not close SoulScreen - it may be running as administrator. Quit it from
    echo its notification-area icon, then run this again.
    goto :fail
)

if not exist "%APP_DIR%\%APP_EXE%" set "FIRST_RUN=1"

rem /MIR also removes files the build no longer produces. Robocopy exit codes below 8 all
rem mean success.
robocopy "%BUILD_DIR%" "%APP_DIR%" /MIR /R:5 /W:1 /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 (
    echo Could not copy the build into artifacts\run\app.
    goto :fail
)

echo Starting SoulScreen...
start "" /D "%APP_DIR%" "%APP_DIR%\%APP_EXE%" %*

if defined FIRST_RUN (
    echo.
    echo This is SoulScreen's first start from artifacts\run\app. If Windows Firewall asks
    echo about it, allow it on the network the iPhone uses - Public included, if that is
    echo how your Wi-Fi is set - or the phone will not be able to connect.
    echo.
    pause
)

:done
popd
endlocal
exit /b 0

:fail
echo.
pause
popd
endlocal
exit /b 1

rem ---------------------------------------------------------------------- helpers

rem SoulScreen keeps to one copy per user - starting a second only brings the first to
rem the front - so the running copy has to go before the new build can start.
rem Exit code: 0 gone or never running, 1 would not close, 2 the user kept it.
:close_running_copy
call :is_running || exit /b 0
echo.
echo SoulScreen is already running. Closing it ends the mirroring session and any recording.
choice /C YN /M "Close it and start the new build"
if errorlevel 2 exit /b 2
if not errorlevel 1 exit /b 2

rem A polite close first, so it saves its settings and removes its tray icon. With
rem close-to-tray on that only hides the window, so fall back to ending the process.
taskkill /IM "%APP_EXE%" >nul 2>&1
call :wait_for_exit 10 && exit /b 0
taskkill /F /IM "%APP_EXE%" >nul 2>&1
call :wait_for_exit 5 && exit /b 0
exit /b 1

rem Waits up to %1 seconds for the app to exit. Exit code 0 once it has.
:wait_for_exit
for /L %%i in (1,1,%~1) do (
    call :is_running || exit /b 0
    ping -n 2 127.0.0.1 >nul
)
call :is_running || exit /b 0
exit /b 1

:is_running
tasklist /FI "IMAGENAME eq %APP_EXE%" /NH 2>nul | findstr /I /C:"%APP_EXE%" >nul
exit /b %errorlevel%
