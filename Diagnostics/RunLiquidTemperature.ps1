param([string]$ArtifactRoot='artifacts/liquid-temperature-20261004/final',[switch]$Baseline,[switch]$NoPoolBalance,[string]$Executable,[string]$CoreMaterials='')
$ErrorActionPreference='Stop'
# Iterator fixture: live Update/Draw; logical steps are batched, not a tempo benchmark.
$taskRepo=Split-Path -Parent $PSScriptRoot
if(Get-Process Phyxel -ErrorAction SilentlyContinue){throw 'Save and close the game before diagnostics.'}
$taskDir=[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))
New-Item -ItemType Directory -Force $taskDir | Out-Null
if(-not $Executable){$Executable=Join-Path $taskRepo 'bin/Debug/net8.0-windows/Phyxel.exe'}
$taskSaved=@{}
Get-ChildItem Env: | Where-Object Name -Like 'PHYXEL_*' | ForEach-Object {$taskSaved[$_.Name]=$_.Value;[Environment]::SetEnvironmentVariable($_.Name,$null)}
try {
 $env:PHYXEL_VERIFY_LIQUID_TEMPERATURE='1';$env:PHYXEL_WINDOWED='1';$env:PHYXEL_ARTIFACT_DIR=$taskDir
 if($Baseline){$env:PHYXEL_LIQUID_TEMPERATURE_BASELINE='1'}
 if($NoPoolBalance){$env:PHYXEL_DISABLE_POOL_BALANCE='1'}
 if($CoreMaterials){$env:PHYXEL_CORE_MATERIALS_PATH=[IO.Path]::GetFullPath($CoreMaterials,$taskRepo)}
 $taskProcess=Start-Process -FilePath $Executable -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/error.log"
 Get-Content "$taskDir/run.log" | Where-Object {$_ -like 'PHYXEL_LT_*' -or $_ -like 'PHYXEL_LIQUID_TEMPERATURE_*'}
 if($taskProcess.ExitCode -ne 0){throw 'Liquid temperature checks failed.'}
 if(-not((Get-Content "$taskDir/run.log") -match '^PHYXEL_LIQUID_TEMPERATURE_SUCCESS ')){throw 'Run stopped before completion marker'}
} finally {
 Get-ChildItem Env: | Where-Object Name -Like 'PHYXEL_*' | ForEach-Object {[Environment]::SetEnvironmentVariable($_.Name,$null)}
 foreach($taskKey in $taskSaved.Keys){[Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey])}
}
