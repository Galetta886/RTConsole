Set-StrictMode -Version Latest

function Get-FreeTcpPort {
  $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
  $listener.Start()
  try { return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port }
  finally { $listener.Stop() }
}

function New-TestContext {
  param([Parameter(Mandatory=$true)][string]$Name)

  $repo = Split-Path $PSScriptRoot -Parent
  $builtApp = Join-Path $repo 'artifacts\app'
  $builtStub = Join-Path $repo 'artifacts\test\RTServer.exe'
  if (-not (Test-Path -LiteralPath (Join-Path $builtApp 'RTConsole.exe')) -or
      -not (Test-Path -LiteralPath $builtStub)) {
    throw '测试产物不存在，请先运行 build.ps1'
  }

  $root = Join-Path ([IO.Path]::GetTempPath()) ("RTConsole-tests-{0}-{1}" -f $Name,[Guid]::NewGuid().ToString('N'))
  $app = Join-Path $root 'app'
  $server = Join-Path $root 'server'
  New-Item -ItemType Directory -Force -Path $app,$server | Out-Null
  Copy-Item -LiteralPath (Join-Path $builtApp 'RTConsole.exe'),(Join-Path $builtApp 'panel.html') -Destination $app
  Copy-Item -LiteralPath $builtStub -Destination (Join-Path $server 'RTServer.exe')

  $panelPort = Get-FreeTcpPort
  do { $gamePort = Get-FreeTcpPort } while ($gamePort -eq $panelPort)
  $token = 'test-' + [Guid]::NewGuid().ToString('N')
  $cfg = [ordered]@{
    ServerDir = '..\server'
    Port = $panelPort
    Token = $token
    AutoStart = $false
    OpenBrowser = $false
  } | ConvertTo-Json
  Set-Content -LiteralPath (Join-Path $app 'rtconsole.json') -Value $cfg -Encoding UTF8

  $proc = Start-Process -FilePath (Join-Path $app 'RTConsole.exe') -WorkingDirectory $app -PassThru
  return [pscustomobject]@{
    Root = $root
    App = $app
    Server = $server
    PanelPort = $panelPort
    GamePort = $gamePort
    Token = $token
    BaseUrl = "http://localhost:$panelPort"
    Process = $proc
  }
}

function Invoke-TestApi {
  param(
    [Parameter(Mandatory=$true)]$Context,
    [Parameter(Mandatory=$true)][string]$Path,
    [ValidateSet('GET','POST')][string]$Method = 'GET',
    $Body = $null,
    [int]$TimeoutSec = 30
  )
  $headers = @{ 'X-Token' = $Context.Token }
  $args = @{
    Method = $Method
    Uri = $Context.BaseUrl + $Path
    Headers = $headers
    TimeoutSec = $TimeoutSec
  }
  if ($Method -eq 'POST') {
    $args.ContentType = 'application/json; charset=utf-8'
    $args.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Compress))
  }
  return Invoke-RestMethod @args
}

function Wait-TestPanel {
  param([Parameter(Mandatory=$true)]$Context)
  foreach ($i in 1..30) {
    Start-Sleep -Milliseconds 200
    try { Invoke-TestApi -Context $Context -Path '/api/state' -TimeoutSec 2 | Out-Null; return $true } catch {}
  }
  return $false
}

function Stop-TestContext {
  param([Parameter(Mandatory=$true)]$Context)
  try { Invoke-TestApi -Context $Context -Path '/api/shutdown' -Method POST -Body @{} -TimeoutSec 5 | Out-Null } catch {}
  try { $Context.Process.WaitForExit(5000) | Out-Null } catch {}
  try {
    if (-not $Context.Process.HasExited) { Stop-Process -Id $Context.Process.Id -Force -ErrorAction SilentlyContinue }
  } catch {}
  $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
  $resolved = [IO.Path]::GetFullPath([string]$Context.Root)
  $leaf = Split-Path $resolved -Leaf
  if (-not $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
      -not $leaf.StartsWith('RTConsole-tests-', [StringComparison]::Ordinal)) {
    throw "拒绝清理不安全的测试目录: $resolved"
  }
  Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
}

function Write-TestServerConfig {
  param([Parameter(Mandatory=$true)]$Context)
  $configs = Join-Path $Context.Server 'Configs'
  New-Item -ItemType Directory -Force -Path $configs | Out-Null
  Set-Content -LiteralPath (Join-Path $configs 'ServerConfig.json') -Value ('{"Port":' + $Context.GamePort + '}') -Encoding UTF8
}
