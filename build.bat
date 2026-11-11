@echo off
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

if not exist skin.png (
  echo skin.png must be in the same folder as this file.
  pause
  exit /b 1
)

set ICON=
if exist icon.ico set ICON=/win32icon:icon.ico

"%CSC%" /nologo /target:winexe /optimize %ICON% /out:RetroMonitor.exe ^
  /resource:skin.png,skin.png ^
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ^
  RetroMonitor.cs

if errorlevel 1 (
  echo.
  echo Build failed.
  pause
  exit /b 1
)
echo.
echo Built RetroMonitor.exe - double-click it to run.
pause
