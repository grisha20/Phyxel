param([string]$ArtifactRoot='artifacts/pool-level-20261006/final',[switch]$Baseline,[switch]$Matrix,[switch]$Gates,[string]$Ids='')
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
if(Get-Process Phyxel -ErrorAction SilentlyContinue){throw 'Save and close the game before diagnostics.'}
$taskDir=[IO.Path]::GetFullPath($ArtifactRoot,$taskRepo)
New-Item -ItemType Directory -Force $taskDir|Out-Null
$taskSaved=@{};Get-ChildItem Env:PHYXEL_*|ForEach-Object{$taskSaved[$_.Name]=$_.Value;Remove-Item -LiteralPath ('Env:'+$_.Name)}
try{
 $env:PHYXEL_VERIFY_POOL_LEVEL='1';$env:PHYXEL_WINDOWED='1';$env:PHYXEL_ARTIFACT_DIR=$taskDir
 $env:PHYXEL_POOL_BASELINE=([int][bool]$Baseline).ToString();$env:PHYXEL_POOL_MATRIX=([int][bool]$Matrix).ToString()
 if($Baseline){$env:PHYXEL_DISABLE_POOL_BALANCE='1'}
 if($Gates){$env:PHYXEL_POOL_GATES='1'}
 if($Ids){$env:PHYXEL_POOL_IDS=$Ids}
 $taskProcess=Start-Process "$taskRepo/bin/Debug/net8.0-windows/Phyxel.exe" -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/error.log"
 Get-Content "$taskDir/run.log"|Where-Object{$_ -like 'PHYXEL_POOL_*'}
 if($taskProcess.ExitCode -ne 0 -or -not((Get-Content "$taskDir/run.log") -match '^PHYXEL_POOL_COMPLETE')){throw 'Pool checks failed'}
}finally{
 Get-ChildItem Env:PHYXEL_*|ForEach-Object{Remove-Item -LiteralPath ('Env:'+$_.Name)}
 foreach($taskKey in $taskSaved.Keys){[Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey])}
}
