param([string]$ArtifactRoot='artifacts/alloys-20261006')
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
if(Get-Process Phyxel -ErrorAction SilentlyContinue){throw 'Save and close the game before diagnostics.'}
$taskDir=[IO.Path]::GetFullPath($ArtifactRoot,$taskRepo)
New-Item -ItemType Directory -Force $taskDir|Out-Null
$taskSaved=@{};Get-ChildItem Env:PHYXEL_*|ForEach-Object{$taskSaved[$_.Name]=$_.Value;Remove-Item -LiteralPath ('Env:'+$_.Name)}
try{
 $env:PHYXEL_VERIFY_ALLOYS='1';$env:PHYXEL_WINDOWED='1';$env:PHYXEL_ARTIFACT_DIR=$taskDir
 $taskProcess=Start-Process "$taskRepo/bin/Debug/net8.0-windows/Phyxel.exe" -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/error.log"
 Get-Content "$taskDir/run.log"|Where-Object{$_ -like 'PHYXEL_ALLOY_*'}
 if($taskProcess.ExitCode -ne 0 -or -not((Get-Content "$taskDir/run.log") -match '^PHYXEL_ALLOY_COMPLETE')){throw 'Alloy checks failed'}
}finally{
 Get-ChildItem Env:PHYXEL_*|ForEach-Object{Remove-Item -LiteralPath ('Env:'+$_.Name)}
 foreach($taskKey in $taskSaved.Keys){[Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey])}
}
