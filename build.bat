@echo off
setlocal

pushd "%~dp0" || (
    echo Failed to open the project directory.
    exit /b 1
)

set "PAUSE_AFTER=0"

if /i "%~1"=="debug" goto build_debug
if /i "%~1"=="release" goto build_release
if /i "%~1"=="all" goto build_all
if not "%~1"=="" goto usage

set "PAUSE_AFTER=1"
echo Select build configuration:
echo   [D] Debug
echo   [R] Release
echo   [A] Debug and Release
choice /c DRA /n /m "Choice: "
if errorlevel 3 goto build_all
if errorlevel 2 goto build_release
goto build_debug

:build_debug
call :build Debug
goto finish

:build_release
call :build Release
goto finish

:build_all
call :build Debug
if errorlevel 1 goto finish
call :build Release
goto finish

:build
echo.
echo === Building %~1 ===
dotnet build Phyxel.sln -c %~1 --nologo
exit /b %errorlevel%

:usage
echo Usage: build.bat [debug^|release^|all]
set "EXIT_CODE=2"
goto cleanup

:finish
set "EXIT_CODE=%errorlevel%"
if "%EXIT_CODE%"=="0" (
    echo.
    echo Build completed successfully.
) else (
    echo.
    echo Build failed with exit code %EXIT_CODE%.
)

:cleanup
popd
if "%PAUSE_AFTER%"=="1" pause
exit /b %EXIT_CODE%
