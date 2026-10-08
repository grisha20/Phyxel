param(
    [string]$ScenePath,
    [ValidateRange(1,300)][int]$Seconds=30,
    [switch]$NonInteractive
)
$ErrorActionPreference='Stop'
if(Get-Process Phyxel -ErrorAction SilentlyContinue){
    Write-Host 'Save your scene and close Phyxel before profiling.'
    if(!$NonInteractive){Read-Host 'Press Enter'}
    exit 1
}
$taskExe=Join-Path $PSScriptRoot 'Phyxel.exe'
if(!(Test-Path -LiteralPath $taskExe)){throw 'Place Profile-Scene.ps1 beside Phyxel.exe.'}
Add-Type -AssemblyName System.Windows.Forms
if([string]::IsNullOrWhiteSpace($ScenePath)){
    if($NonInteractive){throw 'Pass -ScenePath with a saved scene JSON.'}
    $taskDialog=New-Object System.Windows.Forms.OpenFileDialog
    try {
        $taskDialog.Title='Choose the saved scene with the FPS problem'
        $taskDialog.Filter='Phyxel scene (*.json)|*.json'
        $taskDialog.InitialDirectory=Join-Path $env:LOCALAPPDATA 'Phyxel'
        if($taskDialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK){exit 0}
        $ScenePath=$taskDialog.FileName
    } finally {$taskDialog.Dispose()}
}
$taskScene=(Resolve-Path -LiteralPath $ScenePath).Path
if([IO.Path]::GetExtension($taskScene) -ne '.json'){throw 'Choose the scene JSON, not the WORLD file.'}
$taskWorld=[IO.Path]::ChangeExtension($taskScene,'.world')
if(!(Test-Path -LiteralPath $taskWorld)){throw 'The scene needs its matching WORLD file.'}
$taskSceneHash=(Get-FileHash -LiteralPath $taskScene).Hash
$taskWorldHash=(Get-FileHash -LiteralPath $taskWorld).Hash
$taskDir=Join-Path $env:LOCALAPPDATA ('Phyxel\Benchmarks\Scene-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+$PID)
New-Item -ItemType Directory -Path $taskDir | Out-Null
foreach($taskVariable in Get-ChildItem Env:PHYXEL_*){[Environment]::SetEnvironmentVariable($taskVariable.Name,$null,'Process')}
$env:PHYXEL_ARTIFACT_DIR=$taskDir
$env:PHYXEL_VERIFY_FURNACE_SENSORS='1'
$env:PHYXEL_SENSOR_REALTIME='1'
$env:PHYXEL_SENSOR_SCENE=$taskScene
$env:PHYXEL_WINDOWED='1'
$taskBounds=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$env:PHYXEL_WINDOW_WIDTH=[string]$taskBounds.Width
$env:PHYXEL_WINDOW_HEIGHT=[string]$taskBounds.Height
$env:PHYXEL_CLOCK_TRACE_SECONDS=[string]$Seconds
$env:PHYXEL_FRAME_TRACE=Join-Path $taskDir 'frame.csv'
$env:PHYXEL_SIMULATION_CLOCK_TRACE=Join-Path $taskDir 'clock.csv'
$env:PHYXEL_FIRE_PERFORMANCE='1'
[ordered]@{scene=$taskScene;sceneSha256=$taskSceneHash;worldSha256=$taskWorldHash;
    seconds=$Seconds;windowWidth=$taskBounds.Width;windowHeight=$taskBounds.Height;
    startedAt=(Get-Date -Format o)} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskDir 'profile.json') -Encoding UTF8
Write-Host "Profiling the saved scene for $Seconds seconds after loading. It will run unpaused."
Write-Host 'Mode, physics settings and effects come from the save. The source files are read only.'
$taskProcess=Start-Process -FilePath $taskExe -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru -Wait `
    -RedirectStandardOutput (Join-Path $taskDir 'run.log') -RedirectStandardError (Join-Path $taskDir 'error.log')
if($taskSceneHash -ne (Get-FileHash -LiteralPath $taskScene).Hash -or $taskWorldHash -ne (Get-FileHash -LiteralPath $taskWorld).Hash){
    throw 'The source scene changed during profiling.'
}
Write-Host "Exit code: $($taskProcess.ExitCode)"
Write-Host "Results: $taskDir"
Write-Host 'Share frame.csv, clock.csv and run.log. If it failed, also share error.log.'
if(!$NonInteractive){Read-Host 'Press Enter'}
exit $taskProcess.ExitCode
