param([string]$ArtifactRoot='artifacts/absorption-20261005/final',[switch]$Baseline,[switch]$QuickOnly)
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskDir=[IO.Path]::GetFullPath($ArtifactRoot,$taskRepo)
& (Join-Path $PSScriptRoot 'WarmCellularShader.ps1')
New-Item -ItemType Directory -Force $taskDir|Out-Null
$taskSaved=@{};Get-ChildItem Env:PHYXEL_*|ForEach-Object {$taskSaved[$_.Name]=$_.Value;Remove-Item -LiteralPath ('Env:'+$_.Name)}
try {
 $env:PHYXEL_VERIFY_ABSORPTION='1';$env:PHYXEL_ABSORPTION_BASELINE=([int][bool]$Baseline).ToString()
 $env:PHYXEL_ABSORPTION_QUICK_ONLY=([int][bool]$QuickOnly).ToString()
 $taskLiquidDir=Join-Path $taskDir 'test-liquids';New-Item -ItemType Directory -Force $taskLiquidDir|Out-Null
 foreach($taskNumber in 0..31){
  $taskId='test:absorption_'+$taskNumber.ToString('D2')
  @{schema=1;id=$taskId;name='Test liquid';kind='liquid';color='#8090A0';physics=@{density=1.2};thermal=@{initialTemperature=30;heatCapacity=(1+$taskNumber*.1);conductivity=0};ui=@{hidden=$true;order=500}}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $taskLiquidDir "$taskNumber.json")
 }
 $env:PHYXEL_MATERIALS_PATH=$taskLiquidDir
 $env:PHYXEL_ARTIFACT_DIR=$taskDir;$env:PHYXEL_WINDOWED='1'
 $taskStart=[DateTime]::UtcNow
 $taskP=Start-Process "$taskRepo/bin/Debug/net8.0-windows/Phyxel.exe" -WorkingDirectory $taskRepo -WindowStyle Normal -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/error.log"
 if($taskP.ExitCode -ne 0 -or -not((Get-Content "$taskDir/run.log") -match '^PHYXEL_ABSORPTION_COMPLETE') -or (Get-Item "$taskDir/measurements.json").LastWriteTimeUtc -lt $taskStart){Get-Content "$taskDir/run.log" -Tail 8;Get-Content "$taskDir/error.log";throw 'Absorption run did not pass/complete'}
 Get-Content "$taskDir/run.log" -Tail 1
} finally {
 Get-ChildItem Env:PHYXEL_*|ForEach-Object {Remove-Item -LiteralPath ('Env:'+$_.Name)}
 foreach($taskKey in $taskSaved.Keys){[Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey])}
}
