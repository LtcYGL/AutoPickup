# 发布单文件 exe：dist\AutoPickup.exe
# 用法: pwsh -File publish.ps1 [-SelfContained]
param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root 'dist'
$out  = Join-Path $dist 'publish'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

$common = @(
  'publish', (Join-Path $root 'AutoPickup.csproj'),
  '-c', 'Release', '-r', 'win-x64',
  '-p:PublishSingleFile=true',
  '-p:IncludeNativeLibrariesForSelfExtract=true',
  '-p:EnableCompressionInSingleFile=true',
  '-p:DebugType=none',
  '-p:GenerateDocumentationFile=false',
  '-o', $out
)
if ($SelfContained) { $common += '-p:SelfContained=true' } else { $common += '-p:SelfContained=false' }

Write-Host ('发布中（' + $(if ($SelfContained) { '自包含，体积大但无需装 .NET' } else { '依赖框架，体积小但需已装 .NET 8' }) + '）…')
& dotnet @common
if ($LASTEXITCODE -ne 0) { throw 'publish 失败' }

New-Item -ItemType Directory -Force -Path $dist | Out-Null
Get-ChildItem $dist -Filter 'AutoPickup*' -File | Remove-Item -Force -ErrorAction SilentlyContinue
$exe = Join-Path $out 'AutoPickup.exe'
Copy-Item $exe (Join-Path $dist 'AutoPickup.exe') -Force

$mb = [math]::Round((Get-Item (Join-Path $dist 'AutoPickup.exe')).Length / 1MB, 1)
Write-Host ''
Write-Host ('单文件已生成: ' + (Join-Path $dist 'AutoPickup.exe') + '  (' + $mb + ' MB)')
Write-Host '其余文件都在 publish\ 里，可以忽略（不会用到）。'
