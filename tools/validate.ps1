# Full validation from a clean state: clean (Debug + Release), restore, full rebuilds (Debug + Release),
# tests (Debug + Release), publish, UI smoke test of the published exe. Writes artifacts\validation-log.txt.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools\validate.ps1 [-Dotnet <path to dotnet.exe>]
param([string]$Dotnet)

# Pick the first dotnet.exe that actually has an SDK (a runtime-only dotnet cannot build).
if (-not $Dotnet) {
  $candidates = @((Get-Command dotnet -ErrorAction SilentlyContinue).Source, "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe", "$env:ProgramFiles\dotnet\dotnet.exe") |
    Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique
  $Dotnet = $candidates | Where-Object { (& $_ --list-sdks 2>$null) } | Select-Object -First 1
  if (-not $Dotnet) { throw 'No .NET SDK found. Install the .NET 8 SDK (see README.md).' }
}
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$root = Split-Path $PSScriptRoot -Parent
$sln = Join-Path $root 'Storiva.sln'
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force $artifacts | Out-Null
$log = Join-Path $artifacts 'validation-log.txt'
$utf8 = New-Object Text.UTF8Encoding $false
[IO.File]::WriteAllText($log, "Storiva validation - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')`r`nSDK: $Dotnet`r`n", $utf8)
$failed = @()

function Write-Log([string]$text) {
  Write-Host $text
  [IO.File]::AppendAllText($log, $text + "`r`n", $utf8)
}

function Step([string]$name, [scriptblock]$action) {
  Write-Log "`r`n=== $name"
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $global:LASTEXITCODE = 0
  $output = & $action 2>&1 | Out-String
  $code = $LASTEXITCODE
  [IO.File]::AppendAllText($log, $output.TrimEnd() + "`r`n", $utf8)
  $status = if ($code -eq 0) { 'OK' } else { "FAILED (exit $code)" }
  Write-Log "--- ${name}: $status in $([math]::Round($sw.Elapsed.TotalSeconds, 1)) s"
  if ($code -ne 0) { $script:failed += $name }
}

$publishDir = Join-Path $artifacts 'release'

# A running Storiva.exe locks its files and would make publish fail; never kill it, just stop here.
$running = Get-Process Storiva -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($publishDir, [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
  Write-Log "Storiva is running from $publishDir (PID $($running.Id -join ', ')). Close it and run this script again."
  exit 2
}

$started = Get-Date
Step 'dotnet --version' { & $Dotnet --version }
Step 'clean Debug' { & $Dotnet clean $sln -c Debug -v q --nologo }
Step 'clean Release' { & $Dotnet clean $sln -c Release -v q --nologo }
Step 'restore' { & $Dotnet restore $sln -v q }
Step 'build Debug (full rebuild)' { & $Dotnet build $sln -c Debug --no-restore --no-incremental -v q --nologo }
Step 'test Debug' { & $Dotnet test $sln -c Debug --no-build -v q --nologo }
Step 'build Release (full rebuild)' { & $Dotnet build $sln -c Release --no-restore --no-incremental -v q --nologo }
Step 'test Release' { & $Dotnet test $sln -c Release --no-build -v q --nologo }
Step 'publish Release' { & $Dotnet publish (Join-Path $root 'src\Storiva.App\Storiva.App.csproj') -c Release --no-restore -o $publishDir -v q --nologo }
Step 'verify outputs were rebuilt in this run' {
  $files = 'src\Storiva.App\bin\Debug\net8.0-windows\Storiva.dll', 'src\Storiva.App\bin\Release\net8.0-windows\Storiva.dll', 'artifacts\release\Storiva.exe'
  foreach ($f in $files) {
    $item = Get-Item (Join-Path $root $f)
    $fresh = $item.LastWriteTime -ge $started
    "{0}  {1:yyyy-MM-dd HH:mm:ss}  {2}" -f $f, $item.LastWriteTime, $(if ($fresh) { 'rebuilt' } else { 'STALE' })
    if (-not $fresh) { $global:LASTEXITCODE = 1 }
  }
}
Step 'UI smoke test (published exe, with the loopback fake S3 server)' {
  # The fake S3 host listens on 127.0.0.1 only and uses fixed, fictitious test credentials. It is our own child
  # process and is stopped (stdin closed) when the smoke test ends. No other endpoint is contacted.
  $out = Join-Path $artifacts 'smoke-release'
  $fakeDll = Join-Path $root 'tests\Storiva.FakeS3\bin\Release\net8.0\Storiva.FakeS3.dll'
  $psi = New-Object Diagnostics.ProcessStartInfo $Dotnet, "`"$fakeDll`" --seed"
  $psi.RedirectStandardOutput = $true; $psi.RedirectStandardInput = $true; $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
  $fake = [Diagnostics.Process]::Start($psi)
  try {
    $line = $fake.StandardOutput.ReadLine()
    if ($line -notmatch '^ENDPOINT (http://127\.0\.0\.1:\d+)/?$') { "fake S3 did not start: $line"; $global:LASTEXITCODE = 1; return }
    "fake S3 (loopback) at $($Matches[1])"
    $env:STORIVA_SMOKE_S3_ENDPOINT = $Matches[1]
    $env:STORIVA_SMOKE_S3_KEY = 'STORIVATESTKEY'
    $env:STORIVA_SMOKE_S3_SECRET = 'storiva-fake-secret-not-a-real-credential'
    $p = Start-Process (Join-Path $publishDir 'Storiva.exe') -ArgumentList '--smoke-test', "`"$out`"" -PassThru
    if (-not $p.WaitForExit(300000)) { $p.Kill(); 'TIMEOUT'; $global:LASTEXITCODE = 1; return }
    [IO.File]::ReadAllText((Join-Path $out 'report.txt'))
    $global:LASTEXITCODE = $p.ExitCode
  }
  finally {
    Remove-Item Env:STORIVA_SMOKE_S3_* -ErrorAction SilentlyContinue
    try { $fake.StandardInput.Close() } catch {}
    if (-not $fake.WaitForExit(5000)) { $fake.Kill() }
  }
}
Step 'no plaintext secret in the smoke-test data folder' {
  $data = Join-Path $artifacts 'smoke-release\data'
  $needles = 'storiva-fake-secret-not-a-real-credential', 'STORIVATESTKEY'
  $files = Get-ChildItem $data -Recurse -File
  "$($files.Count) file(s) in $data"
  foreach ($f in $files) {
    $hex = [BitConverter]::ToString([IO.File]::ReadAllBytes($f.FullName))
    foreach ($n in $needles) {
      foreach ($enc in [Text.Encoding]::UTF8, [Text.Encoding]::Unicode) {
        if ($hex.Contains([BitConverter]::ToString($enc.GetBytes($n)))) { "FOUND '$n' in $($f.Name)"; $global:LASTEXITCODE = 1 }
      }
    }
    "{0,-40} {1,8} bytes  no plaintext secret" -f $f.FullName.Substring($data.Length + 1), $f.Length
  }
}

Write-Log "`r`n=== SUMMARY"
if ($failed.Count -eq 0) { Write-Log 'All steps succeeded.'; exit 0 }
Write-Log "Failed steps: $($failed -join ', ')"
exit 1
