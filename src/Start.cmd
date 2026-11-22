@echo off
setlocal
set "NET=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%NET%\csc.exe" set "NET=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319"
pushd "%~dp0"
set "TARGET=winexe"
set "BINARY=QQWindowLab.Gui.R2.exe"
if "%~1"=="" if exist "%BINARY%" goto launch
if "%~1"=="" goto build
if /i "%~1"=="--rebuild" goto build
set "TARGET=exe"
set "BINARY=QQWindowLab.Cli.exe"
:build
"%NET%\csc.exe" /nologo /codepage:65001 /target:%TARGET% /out:%BINARY% /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Accessibility.dll /r:"%NET%\WPF\WindowsBase.dll" /r:"%NET%\WPF\UIAutomationClient.dll" /r:"%NET%\WPF\UIAutomationTypes.dll" *.cs
if errorlevel 1 (
  pause
  exit /b 1
)
:launch
if /i "%TARGET%"=="winexe" (
  start "" "%~dp0%BINARY%"
) else (
  %BINARY% %*
)
exit /b %errorlevel%
