param([string]$ArtifactRoot='artifacts/oil-smoke-flow-20261005/final',[switch]$Baseline,[switch]$Matrix,[ValidatePattern('^(oil|smoke|pure-smoke)(,(oil|smoke|pure-smoke))*$')][string]$Cases='oil,smoke,pure-smoke',[string]$Executable,[ValidateRange(.25,1)][double]$Scale=.25,[switch]$Show)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot;$dir=[IO.Path]::GetFullPath((Join-Path $repo $ArtifactRoot))
if(-not $Executable){$Executable=Join-Path $repo 'bin/Debug/net8.0-windows/Phyxel.exe'}
$Executable=[IO.Path]::GetFullPath($Executable,$repo)
& (Join-Path $PSScriptRoot 'WarmCellularShader.ps1') -Runtime (Split-Path -Parent $Executable)
New-Item -ItemType Directory -Force $dir|Out-Null
$saved=@{};Get-ChildItem Env:PHYXEL_*|ForEach-Object{$saved[$_.Name]=$_.Value;Remove-Item -LiteralPath ('Env:'+$_.Name)}
try{
 $env:PHYXEL_VERIFY_OIL_SMOKE_FLOW='1';$env:PHYXEL_ARTIFACT_DIR=$dir;$env:PHYXEL_OS_CASES=$Cases
 $env:PHYXEL_OS_SCALE=$Scale.ToString([Globalization.CultureInfo]::InvariantCulture)
 $env:PHYXEL_OS_MATRIX=([int][bool]$Matrix).ToString();$env:PHYXEL_OS_BASELINE=([int][bool]$Baseline).ToString()
 $style='Hidden';if($Show){
  $style='Normal';$env:PHYXEL_WINDOWED='1'
  $env:PHYXEL_UI_SCREENSHOT_PATH="$dir/preview.png";$env:PHYXEL_UI_CAPTURE_FRAME='60'
 }
 $started=[DateTime]::UtcNow
 $p=Start-Process $Executable -WorkingDirectory $repo -WindowStyle $style -Wait -PassThru -RedirectStandardOutput "$dir/run.log" -RedirectStandardError "$dir/error.log"
 Get-Content "$dir/run.log"|Where-Object{$_ -like 'PHYXEL_OS_*'}
 if($p.ExitCode -ne 0){throw "Oil/smoke run failed, exit=$($p.ExitCode)"}
 if(-not(Test-Path -LiteralPath "$dir/measurements.json") -or (Get-Item -LiteralPath "$dir/measurements.json").LastWriteTimeUtc -lt $started){throw 'Oil/smoke run was closed before measurements were completed'}
}finally{
 Get-ChildItem Env:PHYXEL_*|ForEach-Object{Remove-Item -LiteralPath ('Env:'+$_.Name)}
 foreach($key in $saved.Keys){[Environment]::SetEnvironmentVariable($key,$saved[$key])}
}
