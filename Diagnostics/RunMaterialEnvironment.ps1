param([string]$ArtifactRoot='artifacts/material-environment',[switch]$Baseline,[switch]$AirOnly)
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskDir=[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))
New-Item -ItemType Directory -Force $taskDir | Out-Null
$taskSaved=@{};Get-ChildItem Env:PHYXEL* | ForEach-Object {$taskSaved[$_.Name]=$_.Value;Remove-Item -LiteralPath "Env:$($_.Name)"}
try {
 $env:PHYXEL_ARTIFACT_DIR=$taskDir;$env:PHYXEL_WINDOWED='1'
 if($AirOnly){$env:PHYXEL_VERIFY_AIR_HEAT='1'}else{$env:PHYXEL_VERIFY_MATERIAL_ENVIRONMENT='1'}
 if($Baseline){$env:PHYXEL_ENVIRONMENT_BASELINE='1'}
 $taskP=Start-Process (Join-Path $taskRepo 'bin/Debug/net8.0-windows/Phyxel.exe') -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/error.log"
 Get-Content "$taskDir/run.log" | Where-Object {$_ -match '^PHYXEL_(ENVIRONMENT|AIR_HEAT)'}
 if($taskP.ExitCode -ne 0){Get-Content "$taskDir/error.log" -Tail 8;throw 'Material environment failed'}
}finally{
 Get-ChildItem Env:PHYXEL* | ForEach-Object {Remove-Item -LiteralPath "Env:$($_.Name)"}
 foreach($taskKey in $taskSaved.Keys){[Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey])}
}
