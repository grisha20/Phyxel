param([string]$ArtifactRoot='artifacts/air-inventory',[switch]$Baseline)
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskRoot=[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))
New-Item -ItemType Directory -Force -Path $taskRoot | Out-Null
$taskNames=@('PHYXEL_VERIFY_AIR_INVENTORY','PHYXEL_ARTIFACT_DIR','PHYXEL_WINDOWED','PHYXEL_WINDOW_WIDTH','PHYXEL_WINDOW_HEIGHT','PHYXEL_ACCEPTANCE_MODE','PHYXEL_VERIFY_AIR_MODE_SWITCH','PHYXEL_UI_SCREENSHOT_PATH','PHYXEL_CORE_MATERIALS_PATH','PHYXEL_MATERIALS_PATH')
$taskSaved=@{};foreach($taskName in $taskNames){$taskSaved[$taskName]=[Environment]::GetEnvironmentVariable($taskName,'Process')}
try {
 $taskExe=Join-Path $taskRepo 'bin/Debug/net8.0-windows/Phyxel.exe'
 if($Baseline){
  $taskRuntime=Join-Path $taskRoot 'baseline-runtime';New-Item -ItemType Directory -Force $taskRuntime | Out-Null
  Copy-Item -Path (Join-Path $taskRepo 'bin/Debug/net8.0-windows/*') -Destination $taskRuntime -Recurse -Force
  $taskOld=@(& git -C $taskRepo show '0583f2a:Content/Shaders/OxidizerTransport.hlsl') -join "`n"
  if($LASTEXITCODE -ne 0){throw 'Cannot read pre-conservation transport shader'}
  # The extra flux entry does nothing; the old physical transport still clips
  # inventory by capacity. Require a physical balance failure, not a compile error.
  $taskOld += "`n[numthreads(16,16,1)] void CSFlux(uint3 tid : SV_DispatchThreadID) { }`n"
  [IO.File]::WriteAllText((Join-Path $taskRuntime 'Content/Shaders/OxidizerTransport.hlsl'),$taskOld,[Text.UTF8Encoding]::new($false))
  $taskExe=Join-Path $taskRuntime 'Phyxel.exe'
 }
 $env:PHYXEL_VERIFY_AIR_INVENTORY='1';$env:PHYXEL_ARTIFACT_DIR=$taskRoot
 $env:PHYXEL_WINDOWED='1';$env:PHYXEL_WINDOW_WIDTH='1280';$env:PHYXEL_WINDOW_HEIGHT='720'
 $env:PHYXEL_ACCEPTANCE_MODE=$null;$env:PHYXEL_VERIFY_AIR_MODE_SWITCH=$null
 $env:PHYXEL_UI_SCREENSHOT_PATH=$null;$env:PHYXEL_CORE_MATERIALS_PATH=$null;$env:PHYXEL_MATERIALS_PATH=$null
 $taskP=Start-Process $taskExe -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskRoot/run.log" -RedirectStandardError "$taskRoot/error.log"
 Get-Content "$taskRoot/run.log" | Where-Object {$_ -match '^PHYXEL_AIR_'}
 if($Baseline){
  if($taskP.ExitCode -ne 1 -or (Get-Content "$taskRoot/run.log" -Raw) -notmatch 'lost stock'){throw 'Baseline did not reproduce physical inventory loss'}
 }elseif($taskP.ExitCode -ne 0){Get-Content "$taskRoot/error.log";throw 'Air inventory regression failed'}
}finally{foreach($taskName in $taskNames){[Environment]::SetEnvironmentVariable($taskName,$taskSaved[$taskName],'Process')}}
