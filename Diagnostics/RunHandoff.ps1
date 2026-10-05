param(
 [string]$ArtifactRoot='artifacts/handoff-20261005/final',
 [ValidatePattern('^(gas|parcel|probe|oil|fuel|furnace|furnace-feed|performance)(,(gas|parcel|probe|oil|fuel|furnace|furnace-feed|performance))*$')]
 [string]$Cases='gas,parcel,probe,oil,fuel,furnace,performance',
 [switch]$Baseline,[switch]$Matrix,[switch]$Trace,
 [ValidateRange(-100,286)][float]$OilTemperature=30,
 [ValidateRange(30,1000)][int]$PerformanceFps=100,
 [string]$Executable='bin/Debug/net8.0-windows/Phyxel.exe'
)
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskDir=[IO.Path]::GetFullPath($ArtifactRoot,$taskRepo)
$taskExe=[IO.Path]::GetFullPath($Executable,$taskRepo)
& (Join-Path $PSScriptRoot 'WarmCellularShader.ps1') -Runtime (Split-Path -Parent $taskExe)
New-Item -ItemType Directory -Force $taskDir | Out-Null
$taskSaved=@{}; Get-ChildItem Env:PHYXEL_* | ForEach-Object {$taskSaved[$_.Name]=$_.Value;Remove-Item -LiteralPath ('Env:'+$_.Name)}
try {
 $env:PHYXEL_VERIFY_HANDOFF='1';$env:PHYXEL_HANDOFF_CASES=$Cases
 $env:PHYXEL_HANDOFF_BASELINE=([int][bool]$Baseline).ToString();$env:PHYXEL_HANDOFF_MATRIX=([int][bool]$Matrix).ToString()
 $env:PHYXEL_HANDOFF_OIL_TEMPERATURE=$OilTemperature.ToString([Globalization.CultureInfo]::InvariantCulture)
 $env:PHYXEL_HANDOFF_PERFORMANCE_FPS=$PerformanceFps.ToString()
 $env:PHYXEL_ARTIFACT_DIR=$taskDir;$env:PHYXEL_WINDOWED='1'
 $env:PHYXEL_TRACE_NONFINITE=([int][bool]$Trace).ToString()
 $env:PHYXEL_WINDOW_WIDTH='1280';$env:PHYXEL_WINDOW_HEIGHT='720'
 $taskStarted=[DateTime]::UtcNow
 $taskProcess=Start-Process $taskExe -WorkingDirectory $taskRepo -WindowStyle Normal -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/error.log"
 if($taskProcess.ExitCode -ne 0){Get-Content "$taskDir/run.log" -Tail 10;throw "Handoff run failed: $($taskProcess.ExitCode)"}
 if(-not(Test-Path "$taskDir/measurements.json") -or (Get-Item "$taskDir/measurements.json").LastWriteTimeUtc -lt $taskStarted){throw 'Run stopped before measurements'}
 if(-not((Get-Content "$taskDir/run.log") -match '^PHYXEL_HANDOFF_COMPLETE ')){throw 'Run stopped before completion'}
 Get-Content "$taskDir/run.log" | Where-Object {$_ -like 'PHYXEL_HANDOFF *'}
} finally {
 Get-ChildItem Env:PHYXEL_* | ForEach-Object {Remove-Item -LiteralPath ('Env:'+$_.Name)}
 foreach($taskKey in $taskSaved.Keys){[Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey])}
}
