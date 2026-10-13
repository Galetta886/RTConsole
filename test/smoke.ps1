$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestHarness.ps1')

$pass = 0
$fail = 0
$ctx = $null

function Check($name, $cond) {
  if ($cond) { $script:pass++; Write-Host "PASS  $name" }
  else { $script:fail++; Write-Host "FAIL  $name" }
}

try {
  $ctx = New-TestContext -Name 'smoke'
  Write-TestServerConfig -Context $ctx
  Check 'RTConsole 面板端口就绪' (Wait-TestPanel -Context $ctx)

  try {
    Invoke-RestMethod -Uri ($ctx.BaseUrl + '/api/state') -TimeoutSec 3 | Out-Null
    $unauthorized = $false
  } catch { $unauthorized = ($_.Exception.Response.StatusCode.value__ -eq 401) }
  Check '无 token 访问被拒 (401)' $unauthorized

  $s = Invoke-TestApi -Context $ctx -Path '/api/state'
  Check '初始 running=false' ($s.running -eq $false)

  Invoke-TestApi -Context $ctx -Path '/api/start' -Method POST -Body @{} | Out-Null
  $started = $false
  foreach ($i in 1..20) {
    Start-Sleep -Milliseconds 250
    $s = Invoke-TestApi -Context $ctx -Path '/api/state'
    if ($s.running) { $started = $true; break }
  }
  Check '托管启动假服务器' $started

  $gotUser = $false
  foreach ($i in 1..20) {
    Start-Sleep -Milliseconds 250
    $s = Invoke-TestApi -Context $ctx -Path '/api/state'
    if ($s.online.Count -gt 0 -and $s.online[0].n -eq '测试玩家') { $gotUser = $true; break }
  }
  Check '登录行解析 + 中文名 UTF-8' $gotUser

  Invoke-TestApi -Context $ctx -Path '/api/cmd' -Method POST -Body @{ cmd='list' } | Out-Null
  $seen = $false
  foreach ($i in 1..10) {
    Start-Sleep -Milliseconds 250
    $s = Invoke-TestApi -Context $ctx -Path '/api/state'
    if (($s.lines -join "`n").Contains('Connected players: [1]')) { $seen = $true; break }
  }
  Check 'stdin 命令 -> stdout 回读' $seen

  try {
    Invoke-TestApi -Context $ctx -Path '/api/cmd' -Method POST -Body @{ cmd='resetserver' } | Out-Null
    $guard = $false
  } catch { $guard = ($_.Exception.Response.StatusCode.value__ -eq 400) }
  Check 'resetserver 无确认被拒 (400)' $guard

  Invoke-TestApi -Context $ctx -Path '/api/cmd' -Method POST -Body @{ cmd='eventlist' } | Out-Null
  Start-Sleep -Milliseconds 500
  $s = Invoke-TestApi -Context $ctx -Path '/api/state'
  Check 'eventlist 解析进下拉' (($s.events -join ',').Contains('RaidEnemy'))

  Invoke-TestApi -Context $ctx -Path '/api/backup' -Method POST -Body @{} | Out-Null
  $zip = $null
  foreach ($i in 1..20) {
    Start-Sleep -Milliseconds 250
    $bk = Invoke-TestApi -Context $ctx -Path '/api/backups'
    if ($bk.backups.Count -gt 0) { $zip = $bk.backups[0].n; break }
  }
  Check 'backup 命令产生 zip 并列出' ($null -ne $zip)

  Invoke-TestApi -Context $ctx -Path '/api/stop' -Method POST -Body @{} -TimeoutSec 20 | Out-Null
  $stopped = $false
  foreach ($i in 1..20) {
    Start-Sleep -Milliseconds 250
    $s = Invoke-TestApi -Context $ctx -Path '/api/state'
    if (-not $s.running -and -not $s.extRunning) { $stopped = $true; break }
  }
  Check '服务器完全停止且端口空闲' $stopped
  if (-not $stopped) { throw ('服务器停止状态异常: ' + ($s | ConvertTo-Json -Depth 5 -Compress)) }

  $r = Invoke-TestApi -Context $ctx -Path '/api/restore' -Method POST -Body @{ file=$zip; confirm='RESTORE' } -TimeoutSec 20
  Check '还原执行' ($r.restored -ge 2)
  Check '还原产物落盘 (Assets\Stub.json)' (Test-Path -LiteralPath (Join-Path $ctx.Server 'Assets\Stub.json'))
  $pre = Get-ChildItem -LiteralPath (Join-Path $ctx.Server 'Backups\Servers') -Filter 'PreRestore_*.zip' -ErrorAction SilentlyContinue
  Check '还原前自动快照已生成' (@($pre).Count -ge 1)
}
finally {
  if ($null -ne $ctx) { Stop-TestContext -Context $ctx }
}

Write-Host ""
Write-Host "== 结果: $pass 通过 / $fail 失败 =="
if ($fail -gt 0) { exit 1 }
