param([switch]$NonInteractive)
$ErrorActionPreference='Stop'
if(Get-Process Phyxel -ErrorAction SilentlyContinue){
    Write-Host 'Save your scene and close Phyxel before running the benchmark.'
    if(!$NonInteractive){Read-Host 'Press Enter'}
    exit 1
}
$taskExe=Join-Path $PSScriptRoot 'Phyxel.exe'
if(!(Test-Path -LiteralPath $taskExe)){throw 'Place Profile-GPU.ps1 beside Phyxel.exe.'}
$taskDir=Join-Path $env:LOCALAPPDATA ('Phyxel\Benchmarks\'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+$PID)
New-Item -ItemType Directory -Path $taskDir | Out-Null
Get-ChildItem Env:PHYXEL_* | Remove-Item
$env:PHYXEL_VERIFY_GPU_WORKLOAD='1'
$env:PHYXEL_ARTIFACT_DIR=$taskDir
$env:PHYXEL_WINDOWED='1'
Write-Host 'Measuring the actual GPU. This may take a minute on a slower card.'
$taskProcess=Start-Process -FilePath $taskExe -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru -Wait `
    -RedirectStandardOutput (Join-Path $taskDir 'run.log') -RedirectStandardError (Join-Path $taskDir 'error.log')
Write-Host "Exit code: $($taskProcess.ExitCode)"
Write-Host "Results: $taskDir"
Write-Host 'Share measurements.json and run.log. If it failed, also share error.log.'
if(!$NonInteractive){Read-Host 'Press Enter'}
exit $taskProcess.ExitCode
