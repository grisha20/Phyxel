param([string]$ArtifactRoot='artifacts/gunpowder-20261003/final',[string]$Cases='open,cold,closed,vented,furnace,furnace-control,furnace-sealed',[switch]$Matrix,[switch]$Baseline,[switch]$Unit,[string]$CoreMaterialsPath,[string]$Executable='bin/Debug/net8.0-windows/Phyxel.exe')
$ErrorActionPreference='Stop'
$taskRepo=Split-Path $PSScriptRoot -Parent
$taskRoot=[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))
New-Item -ItemType Directory -Force $taskRoot | Out-Null
$taskEnv=@('PHYXEL_VERIFY_GUNPOWDER','PHYXEL_VERIFY_REACTION_PULSE','PHYXEL_GUNPOWDER_CASES','PHYXEL_GUNPOWDER_MATRIX','PHYXEL_CORE_MATERIALS_PATH','PHYXEL_MATERIALS_PATH','PHYXEL_ARTIFACT_DIR','PHYXEL_WINDOWED','PHYXEL_WINDOW_WIDTH','PHYXEL_WINDOW_HEIGHT')
$taskPrevious=@{};foreach($key in $taskEnv){$taskPrevious[$key]=[Environment]::GetEnvironmentVariable($key)}
try {
 $env:PHYXEL_VERIFY_GUNPOWDER=if($Unit){$null}else{'1'}
 $env:PHYXEL_VERIFY_REACTION_PULSE=if($Unit){'1'}else{$null}
 $env:PHYXEL_GUNPOWDER_CASES=$Cases;$env:PHYXEL_GUNPOWDER_MATRIX=if($Matrix){'1'}else{'0'}
 $env:PHYXEL_CORE_MATERIALS_PATH=if($CoreMaterialsPath){[IO.Path]::GetFullPath($CoreMaterialsPath)}elseif($Baseline){Join-Path $taskRepo 'artifacts/gunpowder-20261003/baseline-materials'}else{$null}
 $taskExternal=Join-Path $taskRoot 'external-materials';New-Item -ItemType Directory -Force $taskExternal | Out-Null
 $env:PHYXEL_MATERIALS_PATH=$taskExternal
 $env:PHYXEL_ARTIFACT_DIR=$taskRoot;$env:PHYXEL_WINDOWED='1';$env:PHYXEL_WINDOW_WIDTH='1280';$env:PHYXEL_WINDOW_HEIGHT='720'
 $taskProcess=Start-Process -FilePath (Join-Path $taskRepo $Executable) -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $taskRoot 'run.log') -RedirectStandardError (Join-Path $taskRoot 'error.log')
 Get-Content (Join-Path $taskRoot 'run.log') -Tail 12
 if($taskProcess.ExitCode -ne 0){throw "Gunpowder run failed: $($taskProcess.ExitCode)"}
} finally {foreach($key in $taskEnv){[Environment]::SetEnvironmentVariable($key,$taskPrevious[$key])}}
