param(
 [string]$ArtifactRoot='artifacts/furnace-draft-20261006/final',
 [string]$Scene='',
 [string]$OldShader='',
 [ValidateRange(1,180)][int]$Seconds=40,
 [switch]$Matrix,[switch]$Powder,[switch]$Baseline,[switch]$HeatTrace,[switch]$EmissionTrace,[switch]$OxygenTrace,[switch]$SurfaceTrace,[switch]$BulkTrace,[switch]$Boiler
)
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
if(Get-Process Phyxel -ErrorAction SilentlyContinue){throw 'Close or save the existing game before GPU diagnostics.'}
$taskDir=[IO.Path]::GetFullPath($ArtifactRoot,$taskRepo)
New-Item -ItemType Directory -Force $taskDir | Out-Null
$taskSaved=@{};Get-ChildItem Env:PHYXEL_* | ForEach-Object {$taskSaved[$_.Name]=$_.Value;Remove-Item -LiteralPath ('Env:'+$_.Name)}
try {
 $env:PHYXEL_VERIFY_HANDOFF='1';$env:PHYXEL_VERIFY_DRAFT='1';$env:PHYXEL_WINDOWED='1'
 $env:PHYXEL_ARTIFACT_DIR=$taskDir;$env:PHYXEL_DRAFT_SECONDS=[string]$Seconds
 $env:PHYXEL_DRAFT_MATRIX=([int][bool]$Matrix).ToString()
 $env:PHYXEL_DRAFT_POWDER=([int][bool]$Powder).ToString()
 $env:PHYXEL_DRAFT_BASELINE=([int][bool]$Baseline).ToString()
 $env:PHYXEL_DRAFT_HEAT_TRACE=([int][bool]$HeatTrace).ToString()
 $env:PHYXEL_DRAFT_EMISSION_TRACE=([int][bool]$EmissionTrace).ToString()
 $env:PHYXEL_DRAFT_OXYGEN_TRACE=([int][bool]$OxygenTrace).ToString()
 $env:PHYXEL_DRAFT_SURFACE_TRACE=([int][bool]$SurfaceTrace).ToString()
 $env:PHYXEL_DRAFT_BULK_TRACE=([int][bool]$BulkTrace).ToString()
 $env:PHYXEL_DRAFT_BOILER=([int][bool]$Boiler).ToString()
 if($Scene){$env:PHYXEL_DRAFT_SCENE=[IO.Path]::GetFullPath($Scene,$taskRepo)}
 if($OldShader){$env:PHYXEL_DRAFT_OLD_SHADER=[IO.Path]::GetFullPath($OldShader,$taskRepo)}
 $taskProcess=Start-Process (Join-Path $taskRepo 'bin/Debug/net8.0-windows/Phyxel.exe') -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/error.log"
 Get-Content "$taskDir/run.log" -Tail 3
 if($taskProcess.ExitCode -ne 0 -or -not((Get-Content "$taskDir/run.log") -match '^PHYXEL_DRAFT_COMPLETE')){throw 'Furnace draft verification failed or stopped'}
}finally{
 Get-ChildItem Env:PHYXEL_* | ForEach-Object {Remove-Item -LiteralPath ('Env:'+$_.Name)}
 foreach($taskKey in $taskSaved.Keys){[Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey])}
}
