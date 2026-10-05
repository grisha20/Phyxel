param([string]$ArtifactRoot='artifacts/oil-locality-20261005/final',[switch]$Baseline,[switch]$Mobile,[switch]$Ice)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$dir=[IO.Path]::GetFullPath((Join-Path $repo $ArtifactRoot))
New-Item -ItemType Directory -Force $dir | Out-Null
$exe=Join-Path $repo 'bin/Debug/net8.0-windows/Phyxel.exe'
if($Baseline){
  $runtime=Join-Path $dir 'baseline-runtime'
  New-Item -ItemType Directory -Force $runtime|Out-Null
  Copy-Item -Path (Join-Path $repo 'bin/Debug/net8.0-windows/*') -Destination $runtime -Recurse -Force
  $source=@(& git -C $repo show 'ac762df:Content/Shaders/CellularAutomataSolver.hlsl') -join "`n"
  if($LASTEXITCODE -ne 0){throw 'Cannot read pre-WL liquid shader'}
  # The current coordinator includes phases58/59; it must be a no-op in this
  # historical shader, whose unknown phases otherwise fall through to diagonals.
  $entry='(void CSMain\(uint3 dispatchThreadId : SV_DispatchThreadID\)\s*\{)'
  if([regex]::Matches($source,$entry).Count -ne 1){throw 'Unexpected legacy cellular entry point'}
  $source=[regex]::Replace($source,$entry,'$1'+"`n    if (SimulationPhase == 58 || SimulationPhase == 59) return;")
  [IO.File]::WriteAllText((Join-Path $runtime 'Content/Shaders/CellularAutomataSolver.hlsl'),$source,[Text.UTF8Encoding]::new($false))
  $exe=Join-Path $runtime 'Phyxel.exe'
}
$saved=@{}; Get-ChildItem Env:PHYXEL_*|ForEach-Object{$saved[$_.Name]=$_.Value;Remove-Item -LiteralPath ('Env:'+$_.Name)}
try{
  $env:PHYXEL_VERIFY_OIL_LOCALITY='1';$env:PHYXEL_ARTIFACT_DIR=$dir
  if($Mobile){$env:PHYXEL_OIL_LOCALITY_MOBILE='1'}
  if($Ice){$env:PHYXEL_OIL_LOCALITY_ICE='1'}
  $p=Start-Process $exe -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$dir/run.log" -RedirectStandardError "$dir/error.log"
  Get-Content "$dir/run.log" | Where-Object {$_ -match '^PHYXEL_(OL_|OIL_LOCALITY)'}
  if(-not $Baseline -and $p.ExitCode -ne 0){throw 'Oil locality failed'}
  if($Baseline -and -not (Test-Path "$dir/measurements.json")){throw 'Baseline did not finish measurements'}
}finally{
  Get-ChildItem Env:PHYXEL_*|ForEach-Object{Remove-Item -LiteralPath ('Env:'+$_.Name)}
  foreach($key in $saved.Keys){[Environment]::SetEnvironmentVariable($key,$saved[$key])}
}
