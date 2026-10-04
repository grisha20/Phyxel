param([string]$ArtifactRoot='artifacts/liquid-temperature-20261004/final',[switch]$Baseline,[string]$Executable)
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskDir=[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))
New-Item -ItemType Directory -Force $taskDir | Out-Null
if(-not $Executable){$Executable=Join-Path $taskRepo 'bin/Debug/net8.0-windows/Phyxel.exe'}
$taskSaved=@{}
Get-ChildItem Env: | Where-Object Name -Like 'PHYXEL_*' | ForEach-Object {$taskSaved[$_.Name]=$_.Value;[Environment]::SetEnvironmentVariable($_.Name,$null)}
try {
 $env:PHYXEL_VERIFY_LIQUID_TEMPERATURE='1';$env:PHYXEL_WINDOWED='1';$env:PHYXEL_ARTIFACT_DIR=$taskDir
 if($Baseline){$env:PHYXEL_LIQUID_TEMPERATURE_BASELINE='1'}
 $taskProcess=Start-Process -FilePath $Executable -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/error.log"
 Get-Content "$taskDir/run.log" | Where-Object {$_ -like 'PHYXEL_LT_*' -or $_ -like 'PHYXEL_LIQUID_TEMPERATURE_*'}
 if($taskProcess.ExitCode -ne 0){throw 'Liquid temperature checks failed.'}
} finally {
 Get-ChildItem Env: | Where-Object Name -Like 'PHYXEL_*' | ForEach-Object {[Environment]::SetEnvironmentVariable($_.Name,$null)}
 foreach($taskKey in $taskSaved.Keys){[Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey])}
}
