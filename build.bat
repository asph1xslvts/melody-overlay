@echo off
rem Builds MelodyOverlay.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
setlocal
cd /d "%~dp0"

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
set "WM=%WINDIR%\System32\WinMetadata"

set "ICON="
if exist icon.ico set "ICON=/win32icon:icon.ico"

"%FW%\csc.exe" /nologo /target:winexe /optimize+ /nowarn:1701,1702 ^
  /out:MelodyOverlay.exe /win32manifest:app.manifest %ICON% ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /r:"%FW%\System.Runtime.dll" ^
  /r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" ^
  /r:"%FW%\System.Threading.Tasks.dll" ^
  /r:"%FW%\System.IO.dll" ^
  /r:"%WM%\Windows.Foundation.winmd" ^
  /r:"%WM%\Windows.Media.winmd" ^
  /r:"%WM%\Windows.Storage.winmd" ^
  MelodyOverlay.cs SettingsForm.cs

if errorlevel 1 (
  echo.
  echo BUILD FAILED
  pause
  exit /b 1
)
echo OK: MelodyOverlay.exe
