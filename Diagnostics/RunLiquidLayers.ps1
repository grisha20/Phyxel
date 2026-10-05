param([string]$ArtifactRoot='artifacts/liquid-layers-20261005/verified',[switch]$Baseline,[switch]$Matrix,
 [ValidatePattern('^(bowl|layers|partition)(,(bowl|layers|partition))*$')][string]$Cases='bowl,layers,partition',
 [string]$Executable,[switch]$Show)
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskDir=[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))
if(-not $Executable){$Executable=Join-Path $taskRepo 'bin/Debug/net8.0-windows/Phyxel.exe'}
$Executable=[IO.Path]::GetFullPath($Executable,$taskRepo)
& (Join-Path $PSScriptRoot 'WarmCellularShader.ps1') -Runtime (Split-Path -Parent $Executable)
New-Item -ItemType Directory -Force $taskDir|Out-Null
$taskSaved=@{};Get-ChildItem Env:PHYXEL_*|ForEach-Object{$taskSaved[$_.Name]=$_.Value;Remove-Item -LiteralPath ('Env:'+$_.Name)}
try{
 $env:PHYXEL_VERIFY_LIQUID_LAYERS='1';$env:PHYXEL_ARTIFACT_DIR=$taskDir;$env:PHYXEL_LL_CASES=$Cases
 $env:PHYXEL_LL_MATRIX=([int][bool]$Matrix).ToString();$env:PHYXEL_LL_BASELINE=([int][bool]$Baseline).ToString()
 $taskStyle='Hidden';if($Show){$taskStyle='Normal';$env:PHYXEL_WINDOWED='1';$env:PHYXEL_UI_SCREENSHOT_PATH="$taskDir/preview.png";$env:PHYXEL_UI_CAPTURE_FRAME='60'}
 $taskStarted=[DateTime]::UtcNow
 $taskProcess=Start-Process $Executable -WorkingDirectory $taskRepo -WindowStyle $taskStyle -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/error.log"
 Get-Content "$taskDir/run.log"|Where-Object{$_ -like 'PHYXEL_LL_*'}
 if($taskProcess.ExitCode -ne 0){throw "Layer run failed, exit=$($taskProcess.ExitCode)"}
 if(-not(Test-Path -LiteralPath "$taskDir/measurements.json") -or (Get-Item -LiteralPath "$taskDir/measurements.json").LastWriteTimeUtc -lt $taskStarted){throw 'Layer run closed before completion'}
}finally{
 Get-ChildItem Env:PHYXEL_*|ForEach-Object{Remove-Item -LiteralPath ('Env:'+$_.Name)}
 foreach($taskKey in $taskSaved.Keys){[Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey])}
}
