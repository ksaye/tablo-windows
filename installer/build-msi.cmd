@echo off
REM Build the TabloWindows MSI. Run from the repo root or the installer folder.
REM Requires: .NET SDK + WiX v5 global tool (dotnet tool install --global wix).
setlocal
cd /d "%~dp0"

echo === Publishing self-contained x64 build ===
rmdir /s /q publish 2>nul
dotnet publish ..\src\TabloWindows\TabloWindows.csproj -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=false -o publish
if errorlevel 1 exit /b 1

echo === Building MSI with WiX ===
wix build TabloWindows.wxs -arch x64 -o TabloWindows-1.1.1.msi
if errorlevel 1 exit /b 1

echo === Done: %~dp0TabloWindows-1.1.1.msi ===
dir TabloWindows-1.1.1.msi
