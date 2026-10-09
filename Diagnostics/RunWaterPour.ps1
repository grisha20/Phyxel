param(
 [Parameter(Mandatory=$true)][string]$ArtifactRoot,
 [ValidateRange(20,100)][int]$Fps=100,[string]$Scene='',
 [ValidateSet('Simulation','Sandbox')][string]$Mode='Simulation',
 [ValidateRange(8,30)][int]$Seconds=10,[ValidateRange(1,96)][int]$Radius=33,
 [ValidateRange(400,4096)][int]$Width=968,[ValidateRange(300,2160)][int]$Height=564,
 [ValidateRange(0,1500)][int]$FillHeight=0,
 [switch]$Phases,[switch]$Reference,[switch]$Kernels,[switch]$NoAir,[switch]$NativeReference
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
 $env:PHYXEL_POUR_WIDTH=[string]$Width;$env:PHYXEL_POUR_HEIGHT=[string]$Height
 $env:PHYXEL_POUR_FILL_HEIGHT=[string]$FillHeight
 if($Scene){$env:PHYXEL_SENSOR_SCENE=[IO.Path]::GetFullPath($Scene,$taskRepo)}
 if($Phases){$env:PHYXEL_POUR_PHASES='1'}
 if($Reference){$env:PHYXEL_POOL_REFERENCE='1';$env:PHYXEL_SURFACE_REFERENCE='1'}
 if($Kernels){$env:PHYXEL_VERIFY_LIQUID_KERNELS='1';$env:PHYXEL_DRAFT_HEAT_TRACE='1'}
 if($NoAir){$env:PHYXEL_POUR_AIR='0'}
 if($NativeReference){$env:PHYXEL_NATIVE_REFERENCE='1'}
 $taskProcess=Start-Process "$taskRepo/bin/Debug/net8.0-windows/Phyxel.exe" -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/errors.log"
 $taskLog=Get-Content "$taskDir/run.log"
 $taskVerified=if($Kernels){
  ($taskLog -match '^PHYXEL_LIQUID_KERNELS_SUCCESS') -and ($taskLog -match '^PHYXEL_NATIVE_THERMAL_SUCCESS')
 }else{[bool]($taskLog -match '^PHYXEL_POUR_REPLAY_COMPLETE')}
 if($taskProcess.ExitCode -ne 0 -or -not $taskVerified){
  Get-Content "$taskDir/errors.log";throw 'Water pour checks failed.'
 }
 Get-Content "$taskDir/run.log" -Tail 1
} finally {
 foreach($taskVariable in Get-ChildItem Env:PHYXEL_*){Remove-Item -LiteralPath ('Env:'+$taskVariable.Name)}
 foreach($taskKey in $taskSaved.Keys){[Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey],'Process')}
}
