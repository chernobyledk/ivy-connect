param([string]$Dotnet='dotnet', [switch]$Framework)
$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try {
    $out=Join-Path $PSScriptRoot '_release'
    if(Test-Path -LiteralPath $out){throw 'Output folder already exists. Archive it before rebuilding.'}
    New-Item -ItemType Directory -Path $out | Out-Null
    if($Framework){
        $net="$env:windir\Microsoft.NET\Framework64\v4.0.30319"
        $folder=Join-Path $out '文件夹版'
        $single=Join-Path $out '单文件版'
        foreach($d in @($folder,$single)){New-Item -ItemType Directory -Path $d | Out-Null}
        $main=Join-Path $single '启动器.exe'
        $refs=@('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:Accessibility.dll','/r:System.Security.dll','/r:System.Net.Http.dll','/r:System.Web.Extensions.dll','/r:System.Runtime.Serialization.dll',"/r:$net\WPF\WindowsBase.dll","/r:$net\WPF\UIAutomationClient.dll","/r:$net\WPF\UIAutomationTypes.dll")
        & "$net\csc.exe" /nologo /optimize+ /codepage:65001 /platform:x64 /target:winexe /main:LabEntry "/out:$main" '/win32icon:assets\launcher.ico' '/win32manifest:IvyConnect.manifest' '/resource:assets\launcher.ico,QQPrivateApp.LauncherIcon' '/resource:assets\cover.jpg,QQPrivateApp.DefaultCover' @refs '*.cs'
        if($LASTEXITCODE){throw "Compiler exit $LASTEXITCODE"}
        Copy-Item -LiteralPath $main -Destination $folder
        Copy-Item -LiteralPath 'App.config' -Destination (Join-Path $folder '启动器.exe.config')
        'Windows .NET Framework 4.8 runtime is required for the Framework fallback.' | Set-Content -LiteralPath (Join-Path $out '构建类型.txt') -Encoding UTF8
    } else {
        foreach($kind in @(@('PortableFolder','文件夹版'),@('PortableSingle','单文件版'))){
            $p=$kind[0]
            $dest=Join-Path $out $kind[1]
            & $Dotnet publish IvyConnect.Windows.csproj -c Release -r win-x64 --self-contained true "-p:PublishProfile=$p" -o $dest
            if($LASTEXITCODE){throw "dotnet publish exit $LASTEXITCODE"}
        }
        $single=Join-Path $out '单文件版'
        foreach($file in @(Get-ChildItem -LiteralPath $single -Force)){
            if($file.Name -ne '启动器.exe'){throw "Unexpected single-file publish entry: $($file.Name)"}
        }
        '.NET 10.0.15 Windows Desktop win-x64 self-contained.' | Set-Content -LiteralPath (Join-Path $out '构建类型.txt') -Encoding UTF8
    }
} finally {Pop-Location}
