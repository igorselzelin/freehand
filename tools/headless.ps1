<#
Routine Unity validation and builds WITHOUT a window and WITHOUT touching the foreground:
Unity.exe -batchmode -nographics, log to a file, exit code = result.

  .\tools\headless.ps1 -Task BuildValidate
  .\tools\headless.ps1 -Task Tests -Tests PaintBaseline,MRSurfaces,MRScope,MRFlow,MRDrips,MRPerf,E2ESpray
  .\tools\headless.ps1 -Task BuildApk -ApkPath Builds/freehand-quest-v0.13.0.apk

It REFUSES to start when anything else is using the project:
  - another Unity.exe whose command line names this project (an open Editor), or
  - Temp\UnityLockfile held by another process.
It never stops, closes or activates any process or window. If the Editor is open, close it yourself
(or let it be) and run this afterwards; nothing here will do it for you.
#>
param(
    [Parameter(Mandatory = $true)][ValidateSet("BuildValidate", "Tests", "BuildApk")][string]$Task,
    [string[]]$Tests = @(),
    [string]$ApkPath = "Builds/freehand-quest-headless.apk",
    [string]$Version = "",
    [switch]$Graphics,
    [switch]$Development,
    [string]$UnityExe = "D:\unity\Hub\Editor\6000.6.2f1\Editor\Unity.exe"
)

$project = "D:\freehand\freehand"
$logs = Join-Path $project "Logs\headless"
New-Item -ItemType Directory -Force $logs | Out-Null

# 1. Is the project in use?
$others = Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object { $_.CommandLine -match [regex]::Escape("freehand") }
if ($others) {
    Write-Output "RECUSADO: ja existe um Unity usando este projeto (PID $($others.ProcessId -join ', ')). Nada foi iniciado e nenhum processo foi encerrado."
    exit 2
}

$lock = Join-Path $project "Temp\UnityLockfile"
if (Test-Path $lock) {
    try { $stream = [System.IO.File]::Open($lock, 'Open', 'ReadWrite', 'None'); $stream.Close() }
    catch { Write-Output "RECUSADO: $lock esta travado por outro processo."; exit 2 }
}

# 2. Run.
$log = Join-Path $logs "$Task.log"
$method = switch ($Task) { "BuildValidate" { "HeadlessRunner.BuildAndValidate" } "Tests" { "HeadlessRunner.RunTests" } "BuildApk" { "HeadlessRunner.BuildApk" } }
# -Graphics: still batchmode and still no window, but with a GPU device - for tests that render (screenshots).
$arguments = @("-batchmode") + $(if ($Graphics) { @() } else { @("-nographics") }) + @("-projectPath", $project, "-buildTarget", "Android", "-logFile", $log, "-executeMethod", $method)
if ($Task -eq "Tests") { $arguments += @("-tests", ($Tests -join ",")) }
if ($Task -eq "BuildApk") { $arguments += @("-apkPath", $ApkPath); if ($Version) { $arguments += @("-releaseVersion", $Version) }; if ($Development) { $arguments += @("-development") } }

# -WindowStyle Hidden: no window is created at all.
$process = Start-Process -FilePath $UnityExe -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
Write-Output "exit code: $($process.ExitCode)"; if (-not (Test-Path $log) -or (Get-Item $log).LastWriteTime -lt $process.StartTime) { Write-Output "AVISO: o Unity nao escreveu log nesta execucao" }
Write-Output "log do Unity: $log"
Write-Output "resultado:    $(Join-Path $logs ($(switch ($Task) { 'BuildValidate' { 'build_validate.txt' } 'Tests' { 'tests.txt' } 'BuildApk' { 'build_apk.txt' } })))"
exit $process.ExitCode
