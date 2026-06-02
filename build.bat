@echo off
setlocal

:: All paths are relative to this bat file — works on any machine.
set ROOT=%~dp0
set SLN=%ROOT%Infinity_TestMod.sln
set OUT=%ROOT%Infinity-Beyond\bin\Release
set BUILD=%ROOT%build
set DLL=Beyond_0.0.4_Alpha-0.0.236.dll

echo ========================================
echo  Building Infinity-Beyond Mod
echo ========================================
echo.

dotnet build "%SLN%" -c Release

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo BUILD FAILED. Check errors above.
    pause
    exit /b 1
)

echo.
if exist "%OUT%\%DLL%" (
    echo Build succeeded!
    if not exist "%BUILD%" mkdir "%BUILD%"
    copy /Y "%OUT%\%DLL%" "%BUILD%\"
    echo.
    echo DLL ready at:
    echo %BUILD%\%DLL%
) else (
    echo WARNING: Build OK but DLL not found at:
    echo %OUT%\%DLL%
)

echo.
pause
