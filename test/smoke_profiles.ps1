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
  $ctx = New-TestContext -Name 'profiles'
  Write-TestServerConfig -Context $ctx
  Check '面板就绪' (Wait-TestPanel -Context $ctx)

  New-Item -ItemType Directory -Force -Path (Join-Path $ctx.Server 'Assets'),(Join-Path $ctx.Server 'Configs') | Out-Null
  Set-Content -LiteralPath (Join-Path $ctx.Server 'Assets\ExtraX.json') -Value '{"who":"X"}' -Encoding UTF8
  Set-Content -LiteralPath (Join-Path $ctx.Server 'Configs\ModConfig.json') -Value '{"ModConfigs":[{"ModID":"111"},{"ModID":"222"}]}' -Encoding UTF8
  Write-TestServerConfig -Context $ctx

  Invoke-TestApi -Context $ctx -Path '/api/profile/save' -Method POST -Body @{ name='档案X' } | Out-Null
  Check '停止状态自快照保存 档案X' (Test-Path -LiteralPath (Join-Path $ctx.App 'Profiles\档案X.zip'))

  Remove-Item -LiteralPath (Join-Path $ctx.Server 'Assets\ExtraX.json') -Force
  Set-Content -LiteralPath (Join-Path $ctx.Server 'Assets\Stub.json') -Value '{"who":"Y"}' -Encoding UTF8
  Invoke-TestApi -Context $ctx -Path '/api/profile/save' -Method POST -Body @{ name='档案Y' } | Out-Null
  $profiles = Invoke-TestApi -Context $ctx -Path '/api/profiles'
  $y = $profiles.profiles | Where-Object { $_.n -eq '档案Y.zip' }
  Check '档案Y 存在且模组数=2' ($null -ne $y -and $y.mods -eq 2)

  Invoke-TestApi -Context $ctx -Path '/api/start' -Method POST -Body @{} | Out-Null
  $running = $false
  foreach ($i in 1..20) {
    Start-Sleep -Milliseconds 250
    if ((Invoke-TestApi -Context $ctx -Path '/api/state').running) { $running = $true; break }
  }
  Check '服务器已启动' $running
  $r = Invoke-TestApi -Context $ctx -Path '/api/profile/save' -Method POST -Body @{ name='档案R' } -TimeoutSec 60
  Check '运行中保存 档案R' (Test-Path -LiteralPath (Join-Path $ctx.App 'Profiles\档案R.zip'))
  Check '档案R 模组数=2' ($r.mods -eq 2)
  Invoke-TestApi -Context $ctx -Path '/api/stop' -Method POST -Body @{} | Out-Null

  $r = Invoke-TestApi -Context $ctx -Path '/api/profile/switch' -Method POST -Body @{ file='档案X.zip'; confirm='SWITCH'; autostart='false' }
  Check '切换档案X 成功' $r.ok
  Check '替换语义：ExtraX 已恢复' (Test-Path -LiteralPath (Join-Path $ctx.Server 'Assets\ExtraX.json'))
  Check '替换语义：Stub 已被清掉' (-not (Test-Path -LiteralPath (Join-Path $ctx.Server 'Assets\Stub.json')))

  $r = Invoke-TestApi -Context $ctx -Path '/api/profile/switch' -Method POST -Body @{ file='档案Y.zip'; confirm='SWITCH'; autostart='true' }
  Check '切换档案Y 成功且自动开服' ($r.ok -and $r.started)
  Check '档案Y 内容就位（Stub 恢复）' (Test-Path -LiteralPath (Join-Path $ctx.Server 'Assets\Stub.json'))
  Check '档案Y 替换掉 ExtraX' (-not (Test-Path -LiteralPath (Join-Path $ctx.Server 'Assets\ExtraX.json')))

  $profiles = Invoke-TestApi -Context $ctx -Path '/api/profiles'
  Check '档案列表 3 份' ($profiles.profiles.Count -eq 3)
  Check '当前模组数=2' ($profiles.curMods -eq 2)

  try {
    Invoke-TestApi -Context $ctx -Path '/api/profile/switch' -Method POST -Body @{ file='档案X.zip'; confirm='wrong' } | Out-Null
    $guard = $false
  } catch { $guard = ($_.Exception.Response.StatusCode.value__ -eq 400) }
  Check 'SWITCH 确认词守卫' $guard

  Invoke-TestApi -Context $ctx -Path '/api/profile/delete' -Method POST -Body @{ file='档案R.zip'; confirm='DELETE' } | Out-Null
  $profiles = Invoke-TestApi -Context $ctx -Path '/api/profiles'
  Check '删除档案R' (($profiles.profiles | Where-Object { $_.n -eq '档案R.zip' }) -eq $null)
}
finally {
  if ($null -ne $ctx) { Stop-TestContext -Context $ctx }
}

Write-Host ""
Write-Host "== 结果: $pass 通过 / $fail 失败 =="
if ($fail -gt 0) { exit 1 }
