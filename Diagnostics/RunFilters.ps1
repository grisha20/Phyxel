param([string]$ArtifactRoot='artifacts/filters-20261005/final')
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskDir=[IO.Path]::GetFullPath($ArtifactRoot,$taskRepo)
& (Join-Path $PSScriptRoot 'WarmAllShaders.ps1')
New-Item -ItemType Directory -Force $taskDir|Out-Null
$taskPrevious=@{};Get-ChildItem Env:PHYXEL_*|ForEach-Object{$taskPrevious[$_.Name]=$_.Value;Remove-Item -LiteralPath ('Env:'+$_.Name)}
try{
 $taskLiquid=Join-Path $taskDir 'external';New-Item -ItemType Directory -Force $taskLiquid|Out-Null
 @{schema=1;id='test:filter_liquid';name='Filter liquid';kind='liquid';color='#8090A0';physics=@{density=1.2};thermal=@{initialTemperature=30;heatCapacity=2;conductivity=0};ui=@{hidden=$true;order=500}}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $taskLiquid 'liquid.json')
 $env:PHYXEL_MATERIALS_PATH=$taskLiquid;$env:PHYXEL_VERIFY_FILTERS='1';$env:PHYXEL_WINDOWED='1';$env:PHYXEL_ARTIFACT_DIR=$taskDir
 $taskStart=[DateTime]::UtcNow
 $taskP=Start-Process "$taskRepo/bin/Debug/net8.0-windows/Phyxel.exe" -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/error.log"
 Get-Content "$taskDir/run.log" -Tail 3
 if($taskP.ExitCode -ne 0 -or -not((Get-Content "$taskDir/run.log") -match '^PHYXEL_FILTER_COMPLETE') -or (Get-Item "$taskDir/measurements.json").LastWriteTimeUtc -lt $taskStart){Get-Content "$taskDir/error.log";throw 'Filter suite did not complete successfully'}
}finally{
 Get-ChildItem Env:PHYXEL_*|ForEach-Object{Remove-Item -LiteralPath ('Env:'+$_.Name)}
 foreach($taskName in $taskPrevious.Keys){[Environment]::SetEnvironmentVariable($taskName,$taskPrevious[$taskName])}
}
