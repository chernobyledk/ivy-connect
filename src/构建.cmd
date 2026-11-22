@echo off
setlocal
set "NET=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%NET%\csc.exe" set "NET=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%NET%\csc.exe" (
  echo .NET Framework C# compiler missing.
  exit /b 1
)
set "OUT=%~1"
if /i "%OUT%"=="--checks" set "OUT="
if not defined OUT set "OUT=%~dp0build"
for %%D in ("%OUT%") do set "OUT=%%~fD"
if not exist "%OUT%\." mkdir "%OUT%" || exit /b 1
pushd "%~dp0" || exit /b 1
if /i "%~1"=="--checks" goto checks

"%NET%\csc.exe" /nologo /codepage:65001 /target:winexe /out:"%OUT%\QQWindowLab.exe" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Accessibility.dll /r:System.Security.dll /r:"%NET%\WPF\WindowsBase.dll" /r:"%NET%\WPF\UIAutomationClient.dll" /r:"%NET%\WPF\UIAutomationTypes.dll" *.cs
if errorlevel 1 goto failure
"%NET%\csc.exe" /nologo /codepage:65001 /target:exe /out:"%OUT%\QQWindowLab.Cli.exe" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Accessibility.dll /r:System.Security.dll /r:"%NET%\WPF\WindowsBase.dll" /r:"%NET%\WPF\UIAutomationClient.dll" /r:"%NET%\WPF\UIAutomationTypes.dll" *.cs
if errorlevel 1 goto failure
popd
exit /b 0

:checks
"%NET%\csc.exe" /nologo /codepage:65001 /target:exe /main:TimelineChecks /out:"%OUT%\TimelineChecks.exe" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Accessibility.dll /r:System.Security.dll /r:"%NET%\WPF\WindowsBase.dll" /r:"%NET%\WPF\UIAutomationClient.dll" /r:"%NET%\WPF\UIAutomationTypes.dll" *.cs "tests\TimelineChecks.cs"
if errorlevel 1 goto failure
"%OUT%\TimelineChecks.exe"
if errorlevel 1 goto failure
popd
exit /b 0

:failure
popd
exit /b 1
