param(
 [Parameter(Mandatory=$true)][string]$ArtifactRoot,
 [ValidateRange(20,100)][int]$Fps=100,[string]$Scene='',
 [ValidateSet('Simulation','Sandbox')][string]$Mode='Simulation',
 [ValidateRange(8,30)][int]$Seconds=10,[ValidateRange(1,64)][int]$Radius=33,
 [switch]$Phases,[switch]$Reference,[switch]$Kernels,[switch]$NoAir
)
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
if(Get-Process Phyxel -ErrorAction SilentlyContinue){throw 'Save and close Phyxel before diagnostics.'}
$taskDir=[IO.Path]::GetFullPath($ArtifactRoot,$taskRepo)
New-Item -ItemType Directory -Force $taskDir|Out-Null
$taskSaved=@{}
foreach($taskVariable in Get-ChildItem Env:PHYXEL_*){$taskSaved[$taskVariable.Name]=$taskVariable.Value;Remove-Item -LiteralPath ('Env:'+$taskVariable.Name)}
try {
 $env:PHYXEL_VERIFY_WATER_POUR='1';$env:PHYXEL_FIRE_PERFORMANCE='1';$env:PHYXEL_WINDOWED='1'
 $env:PHYXEL_ARTIFACT_DIR=$taskDir;$env:PHYXEL_POUR_FPS=[string]$Fps
 $env:PHYXEL_POUR_SECONDS=[string]$Seconds;$env:PHYXEL_POUR_RADIUS=[string]$Radius;$env:PHYXEL_POUR_MODE=$Mode
 if($Scene){$env:PHYXEL_SENSOR_SCENE=[IO.Path]::GetFullPath($Scene,$taskRepo)}
 if($Phases){$env:PHYXEL_POUR_PHASES='1'}
 if($Reference){$env:PHYXEL_POOL_REFERENCE='1';$env:PHYXEL_SURFACE_REFERENCE='1'}
 if($Kernels){$env:PHYXEL_VERIFY_LIQUID_KERNELS='1'}
 if($NoAir){$env:PHYXEL_POUR_AIR='0'}
 $taskProcess=Start-Process "$taskRepo/bin/Debug/net8.0-windows/Phyxel.exe" -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/errors.log"
 $taskMarker=if($Kernels){'PHYXEL_LIQUID_KERNELS_SUCCESS'}else{'PHYXEL_POUR_REPLAY_COMPLETE'}
 if($taskProcess.ExitCode -ne 0 -or -not((Get-Content "$taskDir/run.log") -match $taskMarker)){
  Get-Content "$taskDir/errors.log";throw 'Water pour checks failed.'
 }
 Get-Content "$taskDir/run.log" -Tail 1
} finally {
 foreach($taskVariable in Get-ChildItem Env:PHYXEL_*){Remove-Item -LiteralPath ('Env:'+$taskVariable.Name)}
 foreach($taskKey in $taskSaved.Keys){[Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey],'Process')}
}
