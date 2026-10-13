<#
build-venv.ps1 - the service's own Python venv for bluetti-read: every package from requirements.lock by exact version
and SHA-256 (pip --require-hashes --no-deps), then the library's writer removed for good - its entry points, its script
and writer modules with their compiled copies, and the two import lines that would load it (the reader runs without
them). Read only, forever.

  pwsh -NoProfile -File tools/build-venv.ps1 -VenvDir <dir> [-Python <python.exe>]

Exit: 0 built and read only · 1 pip failed, or a writer piece stayed on disk · 2 could not run (no Python, no lock file).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $VenvDir,
    [string] $Python = 'python'
)
$ErrorActionPreference = 'Stop'
$lock = Join-Path (Split-Path -Parent $PSScriptRoot) 'requirements.lock'
if (-not (Test-Path -LiteralPath $lock)) { Write-Output "venv: COULD NOT RUN: no $lock"; exit 2 }
if (-not (Get-Command $Python -ErrorAction SilentlyContinue)) { Write-Output "venv: COULD NOT RUN: no $Python on PATH"; exit 2 }
$VenvDir = [IO.Path]::GetFullPath($VenvDir)

& $Python -m venv $VenvDir
if ($LASTEXITCODE -ne 0) { Write-Output "venv: python -m venv failed (exit $LASTEXITCODE)"; exit 1 }
$py = Join-Path $VenvDir 'Scripts\python.exe'
& $py -m pip install --quiet --disable-pip-version-check --require-hashes --no-deps -r $lock
if ($LASTEXITCODE -ne 0) { Write-Output "venv: pip refused the lock (exit $LASTEXITCODE)"; exit 1 }

# The writer, named only to be deleted (the service's WriterGuard checks the same pieces at every start).
$lib = Join-Path $VenvDir 'Lib\site-packages\bluetti_bt_lib'
$utf8 = [Text.UTF8Encoding]::new($false)
$left = @()
foreach ($f in @(Get-ChildItem -LiteralPath (Join-Path $VenvDir 'Scripts') -Filter 'bluetti-write*' -File -Force)) { Remove-Item -LiteralPath $f.FullName -Force }
foreach ($rel in 'scripts\bluetti_write.py', 'bluetooth\device_writer.py') { $p = Join-Path $lib $rel; if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force } }
foreach ($dir in @(Get-ChildItem -LiteralPath $lib -Recurse -Directory -Force -Filter '__pycache__')) {
    foreach ($f in @(Get-ChildItem -LiteralPath $dir.FullName -File -Force | Where-Object { $_.Name -match '^(bluetti_write|device_writer)\.' })) { Remove-Item -LiteralPath $f.FullName -Force } }
foreach ($imp in @(@('bluetooth\__init__.py', '^from \.device_writer import \*\s*$'), @('__init__.py', '^\s*DeviceWriter,\s*$'))) {
    $p = Join-Path $lib $imp[0]
    if (Test-Path -LiteralPath $p) {
        $keep = @([IO.File]::ReadAllLines($p) | Where-Object { $_ -notmatch $imp[1] })
        [IO.File]::WriteAllText($p, (($keep -join "`n") + "`n"), $utf8)
    }
}
$left += @(Get-ChildItem -LiteralPath (Join-Path $VenvDir 'Scripts') -Filter 'bluetti-write*' -File -Force)
foreach ($rel in 'scripts\bluetti_write.py', 'bluetooth\device_writer.py') { if (Test-Path -LiteralPath (Join-Path $lib $rel)) { $left += $rel } }
if ($left.Count) { Write-Output "venv: the writer stayed on disk: $($left -join ', ')"; exit 1 }

# The reader still imports without the writer.
& $py -c 'import bluetti_bt_lib' 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Output 'venv: bluetti_bt_lib does not import after the writer was removed'; exit 1 }
if (-not (Test-Path -LiteralPath (Join-Path $VenvDir 'Scripts\bluetti-read.exe'))) { Write-Output 'venv: no bluetti-read.exe'; exit 1 }
Write-Output "venv: ready, read only ($VenvDir)"
exit 0
