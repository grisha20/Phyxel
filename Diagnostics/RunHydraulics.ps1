param([string]$ArtifactRoot='artifacts/hydraulics-20261001/final',[switch]$Single,[switch]$Baseline)
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskRoot=[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))
New-Item -ItemType Directory -Force -Path $taskRoot | Out-Null
$taskExe=Join-Path $taskRepo 'bin/Debug/net8.0-windows/Phyxel.exe'
if($Baseline) {
    $taskRuntime=Join-Path $taskRoot 'baseline-runtime'
    New-Item -ItemType Directory -Force -Path $taskRuntime | Out-Null
    Copy-Item -Path (Join-Path $taskRepo 'bin/Debug/net8.0-windows/*') -Destination $taskRuntime -Recurse -Force
    $taskSource=@(& git -C $taskRepo show 'ead453d:Content/Shaders/CellularAutomataSolver.hlsl') -join "`n"
    if($LASTEXITCODE -ne 0) {throw 'Cannot read pre-fix shader'}
    [IO.File]::WriteAllText((Join-Path $taskRuntime 'Content/Shaders/CellularAutomataSolver.hlsl'),$taskSource,[Text.UTF8Encoding]::new($false))
    $taskExe=Join-Path $taskRuntime 'Phyxel.exe'
}
$taskNames=@('PHYXEL_ACCEPTANCE_MODE','PHYXEL_ACCEPTANCE_SCALE','PHYXEL_ACCEPTANCE_TARGET_FPS',
    'PHYXEL_ACCEPTANCE_AIR','PHYXEL_ACCEPTANCE_OPEN_BOUNDARIES','PHYXEL_ACCEPTANCE_HYDRAULICS',
    'PHYXEL_ACCEPTANCE_CAPTURE_FRAME','PHYXEL_ARTIFACT_DIR','PHYXEL_MATERIALS_PATH','PHYXEL_CORE_MATERIALS_PATH')
$taskSaved=@{}
foreach($taskName in $taskNames) {$taskSaved[$taskName]=[Environment]::GetEnvironmentVariable($taskName,'Process')}
$taskResults=[Collections.Generic.List[object]]::new()
try {
    $env:PHYXEL_ACCEPTANCE_SCALE='0.25'; $env:PHYXEL_ACCEPTANCE_OPEN_BOUNDARIES='0'
    $env:PHYXEL_ACCEPTANCE_HYDRAULICS=$null; $env:PHYXEL_ACCEPTANCE_CAPTURE_FRAME=$null
    $env:PHYXEL_MATERIALS_PATH=$null; $env:PHYXEL_CORE_MATERIALS_PATH=$null
    $taskCases=@(@{Fps=60;Air=0})
    if(-not $Single -and -not $Baseline) {$taskCases+=@(@{Fps=30;Air=0},@{Fps=100;Air=0},@{Fps=60;Air=1})}
    foreach($taskCase in $taskCases) {
        foreach($taskMode in @('hydraulic_surface','hydraulic_balance')) {
            $taskLabel="$taskMode-fps-$($taskCase.Fps)-air-$($taskCase.Air)"
            $taskDir=Join-Path $taskRoot $taskLabel
            New-Item -ItemType Directory -Force -Path $taskDir | Out-Null
            $env:PHYXEL_ACCEPTANCE_MODE=$taskMode; $env:PHYXEL_ARTIFACT_DIR=$taskDir
            $env:PHYXEL_ACCEPTANCE_TARGET_FPS="$($taskCase.Fps)"; $env:PHYXEL_ACCEPTANCE_AIR="$($taskCase.Air)"
            $taskProcess=Start-Process -FilePath $taskExe -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $taskDir 'run.log') -RedirectStandardError (Join-Path $taskDir 'error.log')
            Get-Content (Join-Path $taskDir 'run.log') | Where-Object {$_ -match '^PHYXEL_(HYDRAULIC|ACCEPTANCE_(SUCCESS|FAILED))'} | Write-Output
            $taskExpected=if($Baseline -and $taskMode -eq 'hydraulic_surface') {1} else {0}
            $taskResults.Add([pscustomobject]@{case=$taskLabel;passed=($taskProcess.ExitCode -eq 0);exit=$taskProcess.ExitCode;expectedExit=$taskExpected})
            $taskResults | Export-Csv -LiteralPath (Join-Path $taskRoot 'summary.csv') -NoTypeInformation
            if($taskProcess.ExitCode -ne $taskExpected) {throw "Unexpected result $taskLabel"}
        }
    }
} finally {foreach($taskName in $taskNames) {[Environment]::SetEnvironmentVariable($taskName,$taskSaved[$taskName],'Process')}}
