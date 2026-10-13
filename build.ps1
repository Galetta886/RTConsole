param(
  [string]$OutputDir = (Join-Path $PSScriptRoot 'artifacts')
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw "找不到 csc: $csc" }

$appDir = Join-Path $OutputDir 'app'
$testDir = Join-Path $OutputDir 'test'
New-Item -ItemType Directory -Force -Path $appDir,$testDir | Out-Null

$refs = @(
  '/r:System.dll',
  '/r:System.Core.dll',
  '/r:System.IO.Compression.dll',
  '/r:System.IO.Compression.FileSystem.dll',
  '/r:System.Security.dll',
  '/r:System.Windows.Forms.dll'
)

& $csc /nologo /target:winexe /platform:anycpu /codepage:65001 /optimize+ $refs `
  ('/out:' + (Join-Path $appDir 'RTConsole.exe')) (Join-Path $root 'src\RTConsole.cs')
if ($LASTEXITCODE -ne 0) { throw 'RTConsole 编译失败' }

Copy-Item -LiteralPath (Join-Path $root 'panel.html') -Destination (Join-Path $appDir 'panel.html') -Force
Copy-Item -LiteralPath (Join-Path $root 'rtconsole.example.json') -Destination (Join-Path $appDir 'rtconsole.example.json') -Force
Copy-Item -LiteralPath (Join-Path $root 'README.md'),(Join-Path $root 'LICENSE') -Destination $appDir -Force

& $csc /nologo /target:exe /platform:anycpu /codepage:65001 `
  '/r:System.IO.Compression.dll' '/r:System.IO.Compression.FileSystem.dll' `
  ('/out:' + (Join-Path $testDir 'RTServer.exe')) (Join-Path $root 'test\stubserver\stub.cs')
if ($LASTEXITCODE -ne 0) { throw 'stub 编译失败' }

Write-Host "Build OK -> $appDir"
