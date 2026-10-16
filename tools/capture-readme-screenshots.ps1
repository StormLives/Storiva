# Regenerates the README screenshots in docs\images from the real window, with fictitious data only:
# starts the loopback fake S3 server (tests\Storiva.FakeS3, 127.0.0.1, test credentials, "example-*" buckets),
# runs "Storiva.exe --screenshot-tour" (own data folder, never the user's), copies the PNGs, stops the server.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-readme-screenshots.ps1 [-Configuration Release] [-Dotnet <path>]
param([string]$Configuration = 'Release', [string]$Dotnet)

if (-not $Dotnet) {
  $candidates = @((Get-Command dotnet -ErrorAction SilentlyContinue).Source, "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe", "$env:ProgramFiles\dotnet\dotnet.exe") |
    Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique
  $Dotnet = $candidates | Where-Object { (& $_ --list-sdks 2>$null) } | Select-Object -First 1
  if (-not $Dotnet) { throw 'No .NET SDK found. Install the .NET 8 SDK (see README.md).' }
}
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$root = Split-Path $PSScriptRoot -Parent
& $Dotnet build (Join-Path $root 'Storiva.sln') -c $Configuration -v q --nologo
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

$exe = Join-Path $root "src\Storiva.App\bin\$Configuration\net8.0-windows\Storiva.exe"
$fakeDll = Join-Path $root "tests\Storiva.FakeS3\bin\$Configuration\net8.0\Storiva.FakeS3.dll"
$work = Join-Path $root 'artifacts\readme-screenshots'
$images = Join-Path $root 'docs\images'
New-Item -ItemType Directory -Force $work, $images | Out-Null

$psi = New-Object Diagnostics.ProcessStartInfo $Dotnet, "`"$fakeDll`" --sample"
$psi.RedirectStandardOutput = $true; $psi.RedirectStandardInput = $true; $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
$fake = [Diagnostics.Process]::Start($psi)
try {
  $line = $fake.StandardOutput.ReadLine()
  if ($line -notmatch '^ENDPOINT (http://127\.0\.0\.1:\d+)/?$') { throw "fake S3 did not start: $line" }
  $env:STORIVA_SMOKE_S3_ENDPOINT = $Matches[1]
  $env:STORIVA_SMOKE_S3_KEY = 'STORIVATESTKEY'
  $env:STORIVA_SMOKE_S3_SECRET = 'storiva-fake-secret-not-a-real-credential'
  $p = Start-Process $exe -ArgumentList '--screenshot-tour', "`"$work`"" -PassThru
  if (-not $p.WaitForExit(120000)) { $p.Kill(); throw 'screenshot tour timed out' }
  Get-Content (Join-Path $work 'tour.txt')
  if ($p.ExitCode -ne 0) { throw "screenshot tour failed (exit $($p.ExitCode))" }
  Copy-Item (Join-Path $work 'storiva-*.png') $images -Force
  "Copied to $images"
}
finally {
  Remove-Item Env:STORIVA_SMOKE_S3_* -ErrorAction SilentlyContinue
  try { $fake.StandardInput.Close() } catch {}
  if (-not $fake.WaitForExit(5000)) { $fake.Kill() }
}
