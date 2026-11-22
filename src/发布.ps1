param(
    [string] $Dotnet = 'dotnet',
    [string] $Destination = (Join-Path $PSScriptRoot '_release')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'IvyConnect.Windows.csproj'
$Destination = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $Destination) {
    throw "发布目录已存在，请指定一个新目录。"
}

& $Dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishProfile=PortableFolder -o $Destination
if ($LASTEXITCODE) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }

$entry = Join-Path $Destination 'Ivy connect.exe'
foreach ($file in @($entry, (Join-Path $Destination 'coreclr.dll'), (Join-Path $Destination 'System.Windows.Forms.dll'))) {
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "发布结果缺少 $([IO.Path]::GetFileName($file))" }
}
if ((Get-Item -LiteralPath $entry).VersionInfo.ProductVersion -ne '1.0.0') {
    throw '正式发布入口的版本不符。'
}
