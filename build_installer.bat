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

echo Step 1 of 2: building the program...
set ICON=
if exist icon.ico set ICON=/win32icon:icon.ico

"%CSC%" /nologo /target:winexe /optimize %ICON% /out:RetroMonitor.exe ^
  /resource:skin.png,skin.png ^
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ^
  RetroMonitor.cs
if errorlevel 1 goto fail

echo Step 2 of 2: building the installer...
"%CSC%" /nologo /target:winexe /optimize %ICON% /out:RetroResolutionFPS-Setup.exe ^
  /resource:RetroMonitor.exe,RetroMonitor.exe ^
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ^
  Installer.cs
if errorlevel 1 goto fail

echo.
echo Done. Your installer is RetroResolutionFPS-Setup.exe
pause
exit /b 0

:fail
echo.
echo Build failed.
pause
exit /b 1
