param([string]$ArtifactRoot='artifacts/wood-cycle',[switch]$Baseline,[string]$BaselineCorePath='artifacts/wood-20261004/before-core')
$ErrorActionPreference='Stop'
# Iterator fixture: live Update/Draw; logical steps are batched, not a tempo benchmark.
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskRoot=[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))
New-Item -ItemType Directory -Force $taskRoot | Out-Null
$taskPrevious=@{}
Get-ChildItem Env:PHYXEL* | ForEach-Object {$taskPrevious[$_.Name]=$_.Value;Remove-Item -LiteralPath "Env:$($_.Name)"}
try {
 $env:PHYXEL_VERIFY_WOOD='1';$env:PHYXEL_WINDOWED='1';$env:PHYXEL_ARTIFACT_DIR=$taskRoot
 if($Baseline){
  $env:PHYXEL_WOOD_BASELINE='1'
  $env:PHYXEL_CORE_MATERIALS_PATH=Join-Path $taskRepo $BaselineCorePath
  $env:PHYXEL_MATERIALS_PATH=Join-Path $taskRoot 'empty-external'
  New-Item -ItemType Directory -Force $env:PHYXEL_MATERIALS_PATH | Out-Null
 }
 $taskP=Start-Process (Join-Path $taskRepo 'bin/Debug/net8.0-windows/Phyxel.exe') -WorkingDirectory $taskRepo -WindowStyle Normal -Wait -PassThru -RedirectStandardOutput "$taskRoot/run.log" -RedirectStandardError "$taskRoot/error.log"
 Get-Content "$taskRoot/run.log" | Where-Object {$_ -match '^PHYXEL_WOOD'}
 if($taskP.ExitCode -ne 0){Get-Content "$taskRoot/error.log";throw 'Wood cycle failed'}
 if(-not((Get-Content "$taskRoot/run.log") -match '^PHYXEL_WOOD_RESULT ')){throw 'Run stopped before completion marker'}
 if($Baseline -and -not ((Get-Content "$taskRoot/run.log") -match 'passed=False')){throw 'Old wood did not reproduce failure'}
} finally {
 Get-ChildItem Env:PHYXEL* | ForEach-Object {Remove-Item -LiteralPath "Env:$($_.Name)"}
 foreach($taskName in $taskPrevious.Keys){[Environment]::SetEnvironmentVariable($taskName,$taskPrevious[$taskName])}
}
