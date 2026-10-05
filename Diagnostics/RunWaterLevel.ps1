param([string]$ArtifactRoot='artifacts/water-level',[switch]$Baseline,[switch]$Full,[switch]$Probe,[switch]$FeedOnly)
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskDir=[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))
New-Item -ItemType Directory -Force $taskDir | Out-Null
$taskExe=Join-Path $taskRepo 'bin/Debug/net8.0-windows/Phyxel.exe'
if($Baseline){
 $taskRuntime=Join-Path $taskDir 'baseline-runtime'
 New-Item -ItemType Directory -Force $taskRuntime | Out-Null
 Copy-Item -Path (Join-Path $taskRepo 'bin/Debug/net8.0-windows/*') -Destination $taskRuntime -Recurse -Force
 foreach($taskShader in @('CellularAutomataSolver.hlsl','SolidBodySolver.hlsl')){
  $taskSource=@(& git -C $taskRepo show "ac762df:Content/Shaders/$taskShader") -join "`n"
  if($LASTEXITCODE -ne 0){throw 'Cannot read pre-fix shader'}
  if($taskShader -eq 'CellularAutomataSolver.hlsl'){
   # Current phase58 must not fall through to the old diagonal solver.
   $taskEntry='(void CSMain\(uint3 dispatchThreadId : SV_DispatchThreadID\)\s*\{)'
   if([regex]::Matches($taskSource,$taskEntry).Count -ne 1){throw 'Unexpected legacy cellular entry point'}
   $taskSource=[regex]::Replace($taskSource,$taskEntry,'$1'+"`n    if (SimulationPhase == 58) return;")
  }
  [IO.File]::WriteAllText((Join-Path $taskRuntime "Content/Shaders/$taskShader"),$taskSource,[Text.UTF8Encoding]::new($false))
 }
 $taskExe=Join-Path $taskRuntime 'Phyxel.exe'
}
$taskSaved=@{};Get-ChildItem Env:PHYXEL* | ForEach-Object {$taskSaved[$_.Name]=$_.Value;Remove-Item -LiteralPath "Env:$($_.Name)"}
try {
 $env:PHYXEL_ARTIFACT_DIR=$taskDir;$env:PHYXEL_WINDOWED='1';$env:PHYXEL_VERIFY_WATER_LEVEL='1'
 if($Baseline){$env:PHYXEL_WATER_LEVEL_BASELINE='1'}
 if($Full){$env:PHYXEL_WATER_LEVEL_FULL='1'}
 if($Probe){$env:PHYXEL_WATER_LEVEL_PROBE='1'}
 if($FeedOnly){$env:PHYXEL_WATER_LEVEL_FEED_ONLY='1'}
 $taskP=Start-Process $taskExe -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/error.log"
 Get-Content "$taskDir/run.log" | Where-Object {$_ -match '^PHYXEL_(WL_|WATER_LEVEL)'}
 if($taskP.ExitCode -ne 0){Get-Content "$taskDir/error.log" -Tail 8;throw 'Water level failed'}
}finally{
 Get-ChildItem Env:PHYXEL* | ForEach-Object {Remove-Item -LiteralPath "Env:$($_.Name)"}
 foreach($taskKey in $taskSaved.Keys){[Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey])}
}
