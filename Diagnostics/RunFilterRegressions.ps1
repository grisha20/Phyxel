param([string]$ArtifactRoot='artifacts/filters-20261005/regressions')
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot;$taskRoot=[IO.Path]::GetFullPath($ArtifactRoot,$taskRepo)
New-Item -ItemType Directory -Force $taskRoot|Out-Null
$taskSaved=@{};Get-ChildItem Env:PHYXEL_*|ForEach-Object{$taskSaved[$_.Name]=$_.Value;Remove-Item -LiteralPath ('Env:'+$_.Name)}
try{
 $taskCases=@(
  @{Name='scene-files';Flag='SCENE_FILES';Marker='PHYXEL_SCENE_FILES_SUCCESS'},
  @{Name='ui';Flag='UI';Marker='PHYXEL_UI_REGRESSION_SUCCESS'},
  @{Name='wood';Flag='WOOD';Marker='PHYXEL_WOOD_RESULT passed=True'},
  @{Name='fuel-moisture';Flag='FUEL_MOISTURE';Marker='PHYXEL_FUEL_MOISTURE_RESULT passed=True'},
  @{Name='submerged';Flag='SUBMERGED_HEAPS';Marker='PHYXEL_SUBMERGED_RESULT passed=True'},
  @{Name='body-balance';Flag='BODY_BALANCE';Marker='PHYXEL_BODY_BALANCE_RESULT passed=True'},
  @{Name='oil-locality';Flag='OIL_LOCALITY';Marker='PHYXEL_OIL_LOCALITY checks='},
  @{Name='handoff';Flag='HANDOFF';Marker='PHYXEL_HANDOFF_COMPLETE'},
  @{Name='absorption';Flag='ABSORPTION';Marker='PHYXEL_ABSORPTION_COMPLETE'}
 )
 foreach($taskCase in $taskCases){
  Get-ChildItem Env:PHYXEL_*|ForEach-Object{Remove-Item -LiteralPath ('Env:'+$_.Name)}
  $taskDir=Join-Path $taskRoot $taskCase.Name;New-Item -ItemType Directory -Force $taskDir|Out-Null
  [Environment]::SetEnvironmentVariable('PHYXEL_VERIFY_'+$taskCase.Flag,'1')
  $env:PHYXEL_WINDOWED='1';$env:PHYXEL_ARTIFACT_DIR=$taskDir
  if($taskCase.Name -eq 'oil-locality'){$env:PHYXEL_OIL_LOCALITY_ICE='1'}
  if($taskCase.Name -eq 'handoff'){$env:PHYXEL_HANDOFF_CASES='gas,parcel,probe'}
  if($taskCase.Name -eq 'absorption'){
   $taskExternal=Join-Path $taskDir 'external';New-Item -ItemType Directory -Force $taskExternal|Out-Null
   foreach($taskNumber in 0..31){
    @{schema=1;id=('test:absorption_'+$taskNumber.ToString('D2'));name='Test liquid';kind='liquid';color='#8090A0';physics=@{density=1.2};thermal=@{initialTemperature=30;heatCapacity=(1+$taskNumber*.1);conductivity=0};ui=@{hidden=$true;order=500}}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $taskExternal "$taskNumber.json")
   }
   $env:PHYXEL_MATERIALS_PATH=$taskExternal
  }
  Write-Output "PHYXEL_FILTER_REGRESSION_BEGIN $($taskCase.Name)"
  $taskStarted=[DateTime]::UtcNow
  $taskP=Start-Process "$taskRepo/bin/Debug/net8.0-windows/Phyxel.exe" -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/error.log"
  $taskLog=Get-Content "$taskDir/run.log"
  if($taskP.ExitCode -ne 0 -or -not($taskLog|Select-String -SimpleMatch $taskCase.Marker)){
   $taskLog|Select-Object -Last 8;Get-Content "$taskDir/error.log";throw "Regression failed: $($taskCase.Name)"
  }
  $taskLog|Where-Object{$_ -match '^PHYXEL_.*(SUCCESS|COMPLETE|RESULT|checks=.*failures=)'}|Select-Object -Last 1
 }
 Write-Output 'PHYXEL_FILTER_REGRESSIONS_COMPLETE'
}finally{
 Get-ChildItem Env:PHYXEL_*|ForEach-Object{Remove-Item -LiteralPath ('Env:'+$_.Name)}
 foreach($taskKey in $taskSaved.Keys){[Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey])}
}
