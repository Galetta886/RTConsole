$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestHarness.ps1')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$pass = 0
$fail = 0
$ctx = $null

function Check($name, $cond) {
  if ($cond) { $script:pass++; Write-Host "PASS  $name" }
  else { $script:fail++; Write-Host "FAIL  $name" }
}

function New-TestZip($path, $entries) {
  $stream = New-Object IO.FileStream($path, [IO.FileMode]::Create)
  try {
    $zip = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
      foreach ($item in $entries.GetEnumerator()) {
        $entry = $zip.CreateEntry($item.Key)
        $writer = New-Object IO.StreamWriter($entry.Open(), [Text.Encoding]::UTF8)
        try { $writer.Write([string]$item.Value) } finally { $writer.Dispose() }
      }
    } finally { $zip.Dispose() }
  } finally { $stream.Dispose() }
}

try {
  $ctx = New-TestContext -Name 'security'
  New-Item -ItemType Directory -Force -Path (Join-Path $ctx.Server 'Assets'),(Join-Path $ctx.Server 'Configs'),(Join-Path $ctx.Server 'Backups\Servers') | Out-Null
  Set-Content -LiteralPath (Join-Path $ctx.Server 'Assets\keep.json') -Value '{"keep":true}' -Encoding UTF8
  Write-TestServerConfig -Context $ctx
  Check '面板就绪' (Wait-TestPanel -Context $ctx)

  $escapeTarget = Join-Path $ctx.Root 'escaped.txt'
  $malicious = Join-Path $ctx.Server 'Backups\Servers\malicious.zip'
  New-TestZip $malicious ([ordered]@{
    'Configs/ServerConfig.json' = ('{"Port":' + $ctx.GamePort + '}')
    'Assets/../../escaped.txt' = 'should-not-exist'
  })
  try {
    Invoke-TestApi -Context $ctx -Path '/api/restore' -Method POST -Body @{ file='malicious.zip'; confirm='RESTORE' } | Out-Null
    $traversalRejected = $false
  } catch { $traversalRejected = ($_.Exception.Response.StatusCode.value__ -eq 500) }
  Check 'ZIP 路径穿越被拒绝' $traversalRejected
  Check '越界文件未写入' (-not (Test-Path -LiteralPath $escapeTarget))
  Check '拒绝恶意 ZIP 后原世界未被清空' (Test-Path -LiteralPath (Join-Path $ctx.Server 'Assets\keep.json'))

  $incomplete = Join-Path $ctx.Server 'Backups\Servers\incomplete.zip'
  New-TestZip $incomplete ([ordered]@{ 'Assets/only.json' = '{}' })
  try {
    Invoke-TestApi -Context $ctx -Path '/api/restore' -Method POST -Body @{ file='incomplete.zip'; confirm='RESTORE' } | Out-Null
    $incompleteRejected = $false
  } catch { $incompleteRejected = ($_.Exception.Response.StatusCode.value__ -eq 500) }
  Check '缺少 ServerConfig 的不完整快照被拒绝' $incompleteRejected
  Check '拒绝不完整快照后原世界未被清空' (Test-Path -LiteralPath (Join-Path $ctx.Server 'Assets\keep.json'))

  $pre = Get-ChildItem -LiteralPath (Join-Path $ctx.Server 'Backups\Servers') -Filter 'PreRestore_*.zip' -ErrorAction SilentlyContinue
  Check '验证失败发生在创建保底快照和清空之前' (@($pre).Count -eq 0)
}
finally {
  if ($null -ne $ctx) { Stop-TestContext -Context $ctx }
}

Write-Host ""
Write-Host "== 结果: $pass 通过 / $fail 失败 =="
if ($fail -gt 0) { exit 1 }
