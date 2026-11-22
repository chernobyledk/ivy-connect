@echo off
setlocal
set "NET=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%NET%\csc.exe" set "NET=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319"
pushd "%~dp0"
set "TARGET=winexe"
set "BINARY=QQWindowLab.Gui.R4.exe"
set "GUI_ARGS="
if /i "%~1"=="--self-test" goto selftest
if /i "%~1"=="--continuous" if "%~2"=="" goto continuous
if "%~1"=="" if exist "%BINARY%" goto launch
if "%~1"=="" goto build
if /i "%~1"=="--rebuild" goto build
set "TARGET=exe"
set "BINARY=QQWindowLab.Cli.exe"
goto build
:continuous
set "GUI_ARGS=--continuous"
if exist "%BINARY%" goto launch
goto build
:selftest
set "GUI_ARGS=--self-test"
if /i "%~2"=="--diagnostic" set "GUI_ARGS=--self-test --diagnostic"
if /i "%~2"=="--foreground-invoke-control" set "GUI_ARGS=--self-test --foreground-invoke-control"
if /i "%~2"=="--foreground-keyboard-control" set "GUI_ARGS=--self-test --foreground-keyboard-control"
if /i "%~2"=="--foreground-enter-control" set "GUI_ARGS=--self-test --foreground-enter-control"
:build
"%NET%\csc.exe" /nologo /codepage:65001 /target:%TARGET% /out:%BINARY% /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Accessibility.dll /r:"%NET%\WPF\WindowsBase.dll" /r:"%NET%\WPF\UIAutomationClient.dll" /r:"%NET%\WPF\UIAutomationTypes.dll" *.cs
if errorlevel 1 (
  pause
  exit /b 1
)
:launch
if /i "%TARGET%"=="winexe" (
  start "" "%~dp0%BINARY%" %GUI_ARGS%
) else (
  %BINARY% %*
)
exit /b %errorlevel%
