param([string]$ArtifactRoot='artifacts/undo',[string]$Scene='')
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
if(Get-Process Phyxel -ErrorAction SilentlyContinue){throw 'Close the game before GPU diagnostics'}
$taskDir=[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))
New-Item -ItemType Directory -Force $taskDir | Out-Null
$taskSaved=@{}
Get-ChildItem Env:PHYXEL* | ForEach-Object {$taskSaved[$_.Name]=$_.Value;Remove-Item -LiteralPath "Env:$($_.Name)"}
try{
 $env:PHYXEL_VERIFY_UNDO='1';$env:PHYXEL_WINDOWED='1';$env:PHYXEL_ARTIFACT_DIR=$taskDir
 if($Scene){
  Copy-Item -LiteralPath $Scene -Destination (Join-Path $taskDir 'source.json')
  Copy-Item -LiteralPath ([IO.Path]::ChangeExtension($Scene,'.world')) -Destination (Join-Path $taskDir 'source.world')
  $env:PHYXEL_UNDO_SCENE=Join-Path $taskDir 'source.json'
 }
 $taskP=Start-Process (Join-Path $taskRepo 'bin/Debug/net8.0-windows/Phyxel.exe') -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/error.log"
 Get-Content "$taskDir/run.log" | Where-Object {$_ -like 'PHYXEL_UNDO*'}
 if($taskP.ExitCode -ne 0){Get-Content "$taskDir/run.log" -Tail 25;throw 'Undo checks failed'}
}finally{
 Get-ChildItem Env:PHYXEL* | ForEach-Object {Remove-Item -LiteralPath "Env:$($_.Name)"}
 foreach($taskKey in $taskSaved.Keys){[Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey])}
}
