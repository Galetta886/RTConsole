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
  $ctx = New-TestContext -Name 'world'
  $dirs = 'Assets\Maps','Assets\WorldObjects','Assets\Saves','Assets\Users','Assets\Guilds','Assets\Events','Configs'
  foreach ($d in $dirs) { New-Item -ItemType Directory -Force -Path (Join-Path $ctx.Server $d) | Out-Null }
  Set-Content -LiteralPath (Join-Path $ctx.Server 'Assets\Maps\1.json') -Value '{}'
  Set-Content -LiteralPath (Join-Path $ctx.Server 'Assets\WorldObjects\9.json') -Value '{}'
  Set-Content -LiteralPath (Join-Path $ctx.Server 'Assets\Saves\TestPlayer.json') -Value '{}'
  Set-Content -LiteralPath (Join-Path $ctx.Server 'Assets\Users\TestPlayer.json') -Value '{}'
  Set-Content -LiteralPath (Join-Path $ctx.Server 'Assets\Guilds\G.json') -Value '{}'
  Set-Content -LiteralPath (Join-Path $ctx.Server 'Assets\Events\SomeEvent.json') -Value '{}'
  foreach ($f in 'Rivers.json','Roads.json','WorldValuesFile.json','Market.json','Leaderboard.json') {
    Set-Content -LiteralPath (Join-Path $ctx.Server ('Assets\' + $f)) -Value '{}'
  }
  Set-Content -LiteralPath (Join-Path $ctx.Server 'Assets\IPBans.json') -Value '[]'
  Write-TestServerConfig -Context $ctx

  Check '面板就绪' (Wait-TestPanel -Context $ctx)
  try {
    Invoke-TestApi -Context $ctx -Path '/api/world/reset' -Method POST -Body @{ confirm='wrong' } | Out-Null
    $guard = $false
  } catch { $guard = ($_.Exception.Response.StatusCode.value__ -eq 400) }
  Check 'NEWWORLD 确认词守卫' $guard

  $r = Invoke-TestApi -Context $ctx -Path '/api/world/reset' -Method POST -Body @{ confirm='NEWWORLD'; keepUsers='true' } -TimeoutSec 30
  Check '重开成功且自动开服' ($r.ok -and $r.started)
  Check 'PreNewWorld 保底快照已生成' (@(Get-ChildItem -LiteralPath (Join-Path $ctx.Server 'Backups\Servers') -Filter 'PreNewWorld_*.zip').Count -ge 1)
  Check '地图已清空' (-not (Test-Path -LiteralPath (Join-Path $ctx.Server 'Assets\Maps\1.json')))
  Check '世界对象已清空' (-not (Test-Path -LiteralPath (Join-Path $ctx.Server 'Assets\WorldObjects\9.json')))
  Check '玩家存档已清空' (-not (Test-Path -LiteralPath (Join-Path $ctx.Server 'Assets\Saves\TestPlayer.json')))
  Check '公会已清空' (-not (Test-Path -LiteralPath (Join-Path $ctx.Server 'Assets\Guilds\G.json')))
  Check 'Rivers 已清空' (-not (Test-Path -LiteralPath (Join-Path $ctx.Server 'Assets\Rivers.json')))
  Check 'Market 已清空' (-not (Test-Path -LiteralPath (Join-Path $ctx.Server 'Assets\Market.json')))
  Check 'WorldValues 已清空' (-not (Test-Path -LiteralPath (Join-Path $ctx.Server 'Assets\WorldValuesFile.json')))
  Check '账号保留' (Test-Path -LiteralPath (Join-Path $ctx.Server 'Assets\Users\TestPlayer.json'))
  Check '事件设置保留' (Test-Path -LiteralPath (Join-Path $ctx.Server 'Assets\Events\SomeEvent.json'))
  Check 'IPBans 保留' (Test-Path -LiteralPath (Join-Path $ctx.Server 'Assets\IPBans.json'))
  Check 'Configs 未动' (Test-Path -LiteralPath (Join-Path $ctx.Server 'Configs\ServerConfig.json'))
  Check '空目录仍在 (Maps)' (Test-Path -LiteralPath (Join-Path $ctx.Server 'Assets\Maps'))
  Check '重开后服务器在跑' ((Invoke-TestApi -Context $ctx -Path '/api/state').running)
}
finally {
  if ($null -ne $ctx) { Stop-TestContext -Context $ctx }
}

Write-Host ""
Write-Host "== 结果: $pass 通过 / $fail 失败 =="
if ($fail -gt 0) { exit 1 }
