@echo off
rem Build RadminGrid.exe with the stock .NET Framework 4.x compiler (no admin rights, no SDK needed)
setlocal
cd /d "%~dp0"

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FW%\csc.exe" (
  echo csc.exe from .NET Framework 4.x not found
  exit /b 1
)
set "WPF=%FW%\WPF"

"%FW%\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /out:RadminGrid.exe /win32manifest:src\app.manifest /win32icon:src\app.ico /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xml.dll /r:System.Security.dll /r:System.Web.Extensions.dll /r:"%WPF%\UIAutomationClient.dll" /r:"%WPF%\UIAutomationTypes.dll" /r:"%WPF%\WindowsBase.dll" /r:"%FW%\System.Runtime.dll" /r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" /r:"%WINDIR%\System32\WinMetadata\Windows.Foundation.winmd" /r:"%WINDIR%\System32\WinMetadata\Windows.Graphics.winmd" /r:"%WINDIR%\System32\WinMetadata\Windows.Media.winmd" /r:"%WINDIR%\System32\WinMetadata\Windows.Storage.winmd" /r:"%WINDIR%\System32\WinMetadata\Windows.Globalization.winmd" src\*.cs
if errorlevel 1 (
  echo.
  echo BUILD FAILED
  exit /b 1
)
echo Built: %~dp0RadminGrid.exe