@echo off
setlocal
chcp 65001 > nul
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

"%NET%\csc.exe" /nologo /codepage:65001 /target:winexe /main:LabEntry /win32icon:"assets\launcher.ico" /resource:"assets\launcher.ico",QQPrivateApp.LauncherIcon /out:"%OUT%\Ivy connect.exe" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Accessibility.dll /r:System.Security.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.Runtime.Serialization.dll /r:"%NET%\WPF\WindowsBase.dll" /r:"%NET%\WPF\UIAutomationClient.dll" /r:"%NET%\WPF\UIAutomationTypes.dll" /resource:"assets\cover.jpg",QQPrivateApp.DefaultCover *.cs
if errorlevel 1 goto failure
"%NET%\csc.exe" /nologo /codepage:65001 /target:exe /main:Program /out:"%OUT%\QQWindowLab.Cli.exe" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Accessibility.dll /r:System.Security.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.Runtime.Serialization.dll /r:"%NET%\WPF\WindowsBase.dll" /r:"%NET%\WPF\UIAutomationClient.dll" /r:"%NET%\WPF\UIAutomationTypes.dll" /resource:"assets\cover.jpg",QQPrivateApp.DefaultCover *.cs
if errorlevel 1 goto failure
copy /y "%~dp0App.config" "%OUT%\Ivy connect.exe.config" > nul
if errorlevel 1 goto failure
copy /y "%~dp0App.config" "%OUT%\QQWindowLab.Cli.exe.config" > nul
if errorlevel 1 goto failure
if not exist "%OUT%\assets\." mkdir "%OUT%\assets" || goto failure
copy /y "%~dp0assets\cover.jpg" "%OUT%\assets\cover.jpg" > nul
if errorlevel 1 goto failure
"%NET%\csc.exe" /nologo /codepage:65001 /target:winexe /win32icon:"assets\launcher.ico" /out:"%OUT%\启动器.exe" /r:System.Windows.Forms.dll "launcher\Program.cs" "PackageInfo.cs"
if errorlevel 1 goto failure
copy /y "%~dp0App.config" "%OUT%\启动器.exe.config" > nul
if errorlevel 1 goto failure
if not exist "%OUT%\当前版本\assets\." mkdir "%OUT%\当前版本\assets" || goto failure
copy /y "%OUT%\Ivy connect.exe" "%OUT%\当前版本\Ivy connect.exe" > nul
if errorlevel 1 goto failure
copy /y "%OUT%\Ivy connect.exe.config" "%OUT%\当前版本\Ivy connect.exe.config" > nul
if errorlevel 1 goto failure
copy /y "%OUT%\assets\cover.jpg" "%OUT%\当前版本\assets\cover.jpg" > nul
if errorlevel 1 goto failure
popd
exit /b 0

:checks
"%NET%\csc.exe" /nologo /codepage:65001 /target:exe /main:TimelineChecks /out:"%OUT%\TimelineChecks.exe" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Accessibility.dll /r:System.Security.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.Runtime.Serialization.dll /r:"%NET%\WPF\WindowsBase.dll" /r:"%NET%\WPF\UIAutomationClient.dll" /r:"%NET%\WPF\UIAutomationTypes.dll" /resource:"assets\cover.jpg",QQPrivateApp.DefaultCover *.cs "tests\TimelineChecks.cs"
if errorlevel 1 goto failure
"%OUT%\TimelineChecks.exe"
if not "%errorlevel%"=="0" goto failure
"%NET%\csc.exe" /nologo /codepage:65001 /target:exe /main:PrivateCoreChecks /out:"%OUT%\PrivateCoreChecks.exe" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Accessibility.dll /r:System.Security.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.Runtime.Serialization.dll /r:"%NET%\WPF\WindowsBase.dll" /r:"%NET%\WPF\UIAutomationClient.dll" /r:"%NET%\WPF\UIAutomationTypes.dll" /resource:"assets\cover.jpg",QQPrivateApp.DefaultCover *.cs "tests\PrivateCoreChecks.cs"
if errorlevel 1 goto failure
"%OUT%\PrivateCoreChecks.exe"
if not "%errorlevel%"=="0" goto failure
"%NET%\csc.exe" /nologo /codepage:65001 /target:exe /main:ModelChecks /out:"%OUT%\ModelChecks.exe" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Accessibility.dll /r:System.Security.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.Runtime.Serialization.dll /r:"%NET%\WPF\WindowsBase.dll" /r:"%NET%\WPF\UIAutomationClient.dll" /r:"%NET%\WPF\UIAutomationTypes.dll" /resource:"assets\cover.jpg",QQPrivateApp.DefaultCover *.cs "tests\ModelChecks.cs"
if errorlevel 1 goto failure
"%OUT%\ModelChecks.exe"
if not "%errorlevel%"=="0" goto failure
"%NET%\csc.exe" /nologo /codepage:65001 /target:exe /main:MultimodelChecks /out:"%OUT%\MultimodelChecks.exe" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Accessibility.dll /r:System.Security.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.Runtime.Serialization.dll /r:"%NET%\WPF\WindowsBase.dll" /r:"%NET%\WPF\UIAutomationClient.dll" /r:"%NET%\WPF\UIAutomationTypes.dll" /resource:"assets\cover.jpg",QQPrivateApp.DefaultCover *.cs "tests\MultimodelChecks.cs"
if errorlevel 1 goto failure
copy /y "%~dp0App.config" "%OUT%\MultimodelChecks.exe.config" > nul
if errorlevel 1 goto failure
"%OUT%\MultimodelChecks.exe"
if not "%errorlevel%"=="0" goto failure
"%NET%\csc.exe" /nologo /codepage:65001 /target:exe /main:RouteChecks /out:"%OUT%\RouteChecks.exe" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Accessibility.dll /r:System.Security.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.Runtime.Serialization.dll /r:"%NET%\WPF\WindowsBase.dll" /r:"%NET%\WPF\UIAutomationClient.dll" /r:"%NET%\WPF\UIAutomationTypes.dll" /resource:"assets\cover.jpg",QQPrivateApp.DefaultCover *.cs "tests\RouteChecks.cs"
if errorlevel 1 goto failure
"%OUT%\RouteChecks.exe"
if not "%errorlevel%"=="0" goto failure
"%NET%\csc.exe" /nologo /codepage:65001 /target:exe /main:StartupChecks /out:"%OUT%\StartupChecks.exe" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Accessibility.dll /r:System.Security.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.Runtime.Serialization.dll /r:"%NET%\WPF\WindowsBase.dll" /r:"%NET%\WPF\UIAutomationClient.dll" /r:"%NET%\WPF\UIAutomationTypes.dll" /resource:"assets\cover.jpg",QQPrivateApp.DefaultCover *.cs "tests\StartupChecks.cs"
if errorlevel 1 goto failure
"%OUT%\StartupChecks.exe"
if not "%errorlevel%"=="0" goto failure
"%NET%\csc.exe" /nologo /codepage:65001 /target:exe /main:NumericPresentationChecks /out:"%OUT%\NumericPresentationChecks.exe" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Accessibility.dll /r:System.Security.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.Runtime.Serialization.dll /r:"%NET%\WPF\WindowsBase.dll" /r:"%NET%\WPF\UIAutomationClient.dll" /r:"%NET%\WPF\UIAutomationTypes.dll" /resource:"assets\cover.jpg",QQPrivateApp.DefaultCover *.cs "tests\NumericPresentationChecks.cs"
if errorlevel 1 goto failure
"%OUT%\NumericPresentationChecks.exe"
if not "%errorlevel%"=="0" goto failure
"%NET%\csc.exe" /nologo /codepage:65001 /target:exe /main:ReceiptChecks /out:"%OUT%\ReceiptChecks.exe" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Accessibility.dll /r:System.Security.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.Runtime.Serialization.dll /r:"%NET%\WPF\WindowsBase.dll" /r:"%NET%\WPF\UIAutomationClient.dll" /r:"%NET%\WPF\UIAutomationTypes.dll" /resource:"assets\cover.jpg",QQPrivateApp.DefaultCover *.cs "tests\ReceiptChecks.cs"
if errorlevel 1 goto failure
"%OUT%\ReceiptChecks.exe"
if not "%errorlevel%"=="0" goto failure
popd
exit /b 0

:failure
popd
exit /b 1
