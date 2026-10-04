param([string]$ArtifactRoot='artifacts/open-combustion-20261005/final', [switch]$Quick, [switch]$RenderEffects, [switch]$Baseline)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$root=[IO.Path]::GetFullPath((Join-Path $repo $ArtifactRoot))
$exe=Join-Path $repo 'bin/Debug/net8.0-windows/Phyxel.exe'
if($Baseline){
  $runtime=Join-Path $root 'baseline-runtime';New-Item -ItemType Directory -Force $runtime|Out-Null
  Copy-Item -Path (Join-Path $repo 'bin/Debug/net8.0-windows/*') -Destination $runtime -Recurse -Force
  $source=@(& git -C $repo show '9498bb1:Content/Shaders/OxidizerTransport.hlsl') -join "`n"
  if($LASTEXITCODE -ne 0){throw 'Cannot read baseline oxidizer shader'}
  [IO.File]::WriteAllText((Join-Path $runtime 'Content/Shaders/OxidizerTransport.hlsl'),$source,[Text.UTF8Encoding]::new($false))
  $exe=Join-Path $runtime 'Phyxel.exe'
}
$saved=@{}; Get-ChildItem Env:PHYXEL_* | ForEach-Object { $saved[$_.Name]=$_.Value; Remove-Item -LiteralPath ('Env:'+$_.Name) }
try {
  $rows=@()
  foreach($mode in @('simulation','sandbox')) {
    foreach($fps in $(if($Quick){@(60)}else{@(30,60,100)})) {
      foreach($scene in $(if($mode -eq 'simulation' -and $fps -eq 60){@('fire_open','fire_obstacle')}else{@('fire_open')})) {
        $dir=Join-Path $root "$mode-$fps-$scene"
        New-Item -ItemType Directory -Force $dir | Out-Null
        $env:PHYXEL_ACCEPTANCE_MODE=$scene
        $env:PHYXEL_ACCEPTANCE_SIMULATION_MODE=$mode
        $env:PHYXEL_ACCEPTANCE_SCALE='0.25'
        $env:PHYXEL_ACCEPTANCE_OPEN_BOUNDARIES='1'
        $env:PHYXEL_ACCEPTANCE_TARGET_FPS=[string]$fps
        $env:PHYXEL_ACCEPTANCE_CAPTURE_FRAME=[string]($fps*6-1)
        $env:PHYXEL_FIRE_STATE_DUMP='1'
        $env:PHYXEL_FIRE_OPEN_STRICT='1'
        $env:PHYXEL_ACCEPTANCE_RENDER_EFFECTS=([int][bool]$RenderEffects).ToString()
        $env:PHYXEL_ARTIFACT_DIR=$dir
        $p=Start-Process $exe -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput "$dir/run.log" -RedirectStandardError "$dir/error.log"
        $line=Get-Content "$dir/run.log" | Where-Object {$_ -like 'PHYXEL_FIRE_OPEN *' -or $_ -like 'PHYXEL_FIRE_OBSTACLE *'} | Select-Object -Last 1
        $row=[ordered]@{mode=$mode;fps=$fps;scene=$scene;exit=$p.ExitCode}
        foreach($key in @('fireCells','fireOccupiedH20','fireOccupiedH40','fireWidthH40','contactWidthFraction','smokeAboveLeft','smokeAboveRight')) {
          $row[$key]=$null
          if($line -match "(?:^| )$key=([0-9.-]+)") { $row[$key]=[double]::Parse($Matches[1],[Globalization.CultureInfo]::InvariantCulture) }
        }
        $rows += [pscustomobject]$row
        Write-Output ($row | ConvertTo-Json -Compress)
        if(-not $Baseline -and $p.ExitCode -ne 0){throw "Combustion failed: $mode/$fps/$scene"}
        if(-not $Baseline -and $scene -eq 'fire_obstacle' -and ($row.contactWidthFraction -lt .5 -or $row.smokeAboveLeft -lt 1 -or $row.smokeAboveRight -lt 1)){
          throw 'Obstacle did not spread gas to both ends'
        }
      }
    }
  }
  $rows | Export-Csv "$root/summary.csv" -NoTypeInformation
} finally {
  Get-ChildItem Env:PHYXEL_* | ForEach-Object {Remove-Item -LiteralPath ('Env:'+$_.Name)}
  foreach($key in $saved.Keys){Set-Item -LiteralPath ('Env:'+$key) -Value $saved[$key]}
}
