<#
publish.ps1 - publish BluettiWatcher.exe: NativeAOT (needs the MSVC linker), or with -Fallback a trimmed self-contained
single-file build. The log goes next to the output as publish.log.

  pwsh -NoProfile -File tools/publish.ps1 -Out <dir> [-Fallback]

Exit: 0 published (the exe is there) · 1 the publish failed (last log lines printed) · 2 could not run.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Out,
    [switch] $Fallback
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo 'src\BluettiWatcher\BluettiWatcher.csproj'
if (-not (Test-Path -LiteralPath $proj)) { Write-Output "publish: COULD NOT RUN: no $proj"; exit 2 }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Write-Output 'publish: COULD NOT RUN: dotnet not on PATH'; exit 2 }
# The ILCompiler finds the MSVC linker through vswhere.exe on PATH; the VS installer does not put it there.
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (-not $Fallback -and -not (Get-Command vswhere.exe -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath (Join-Path $installer 'vswhere.exe'))) {
    $env:PATH = "$installer;$env:PATH"
}
$Out = [IO.Path]::GetFullPath($Out)
$null = New-Item -ItemType Directory -Force -Path $Out
$log = Join-Path $Out 'publish.log'
$extra = if ($Fallback) { @('-p:PublishAot=false', '-p:SelfContained=true', '-p:PublishSingleFile=true', '-p:PublishTrimmed=true', '-p:IncludeNativeLibrariesForSelfExtract=true') } else { @() }
& dotnet publish $proj -c Release -r win-x64 -o $Out -nologo @extra *> $log
$code = $LASTEXITCODE
$exe = Join-Path $Out 'BluettiWatcher.exe'
if ($code -ne 0 -or -not (Test-Path -LiteralPath $exe)) {
    Write-Output "publish: FAILED (dotnet exit $code), log $log"
    Get-Content -LiteralPath $log -Tail 25 | Write-Output
    exit 1
}
Write-Output "publish: $(if ($Fallback) { 'trimmed self-contained single file' } else { 'NativeAOT' }) -> $Out"
Get-ChildItem -LiteralPath $Out -File | Where-Object { $_.Extension -in '.exe', '.dll' } | ForEach-Object { '{0,-28} {1,10:N0} bytes' -f $_.Name, $_.Length } | Write-Output
exit 0
