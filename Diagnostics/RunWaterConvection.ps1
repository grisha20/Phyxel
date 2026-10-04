param([string]$ArtifactRoot='artifacts/convection-20261001/trial',[switch]$Baseline,[switch]$Single,
    [int]$Seconds=60,[switch]$Isolate,[string]$RestartPath,
    [int]$TargetFps=60,[int]$Air=0,[int]$Hydraulics=0,[string]$Executable,[string]$CoreSourceDirectory)
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskRoot=[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))
New-Item -ItemType Directory -Force -Path $taskRoot | Out-Null
$taskCore=Join-Path $taskRoot 'core'
New-Item -ItemType Directory -Force -Path $taskCore | Out-Null
if(-not $CoreSourceDirectory){$CoreSourceDirectory=Join-Path $taskRepo 'Materials/core'}
Copy-Item -Path (Join-Path $CoreSourceDirectory '*.json') -Destination $taskCore -Force
$taskFixture=Join-Path $taskCore 'fixture.json'
$taskJson=Get-Content -Raw -LiteralPath $taskFixture | ConvertFrom-Json
$taskJson.thermal.conductivity=0
$taskJson | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $taskFixture -Encoding utf8
if($Isolate) {
    $taskWater=Join-Path $taskCore 'water.json'
    $taskJson=Get-Content -Raw -LiteralPath $taskWater | ConvertFrom-Json
    $taskJson.thermal.conductivity=0
    $taskJson | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $taskWater -Encoding utf8
}
$taskExe=Join-Path $taskRepo 'bin/Debug/net8.0-windows/Phyxel.exe'
if($Executable){$taskExe=[IO.Path]::GetFullPath((Join-Path $taskRepo $Executable))}
if($Baseline) {
    $taskRuntime=Join-Path $taskRoot 'baseline-runtime'
    New-Item -ItemType Directory -Force -Path $taskRuntime | Out-Null
    Copy-Item -Path (Join-Path $taskRepo 'bin/Debug/net8.0-windows/*') -Destination $taskRuntime -Recurse -Force
    # Same executable and original thermal/water solvers; disable only the new pass.
    [IO.File]::WriteAllText((Join-Path $taskRuntime 'Content/Shaders/WaterConvection.hlsl'),
        '[numthreads(16,16,1)] void CSMain(uint3 p : SV_DispatchThreadID) {}')
    $taskExe=Join-Path $taskRuntime 'Phyxel.exe'
}
$taskNames=@('PHYXEL_ACCEPTANCE_MODE','PHYXEL_ACCEPTANCE_SCALE','PHYXEL_ACCEPTANCE_TARGET_FPS',
    'PHYXEL_ACCEPTANCE_AIR','PHYXEL_ACCEPTANCE_OPEN_BOUNDARIES','PHYXEL_ACCEPTANCE_HYDRAULICS',
    'PHYXEL_ACCEPTANCE_CAPTURE_FRAME','PHYXEL_ARTIFACT_DIR','PHYXEL_MATERIALS_PATH','PHYXEL_CORE_MATERIALS_PATH','PHYXEL_CONVECTION_RESTART')
$taskSaved=@{}
foreach($taskName in $taskNames) {$taskSaved[$taskName]=[Environment]::GetEnvironmentVariable($taskName,'Process')}
$taskResults=[Collections.Generic.List[object]]::new()
try {
    $env:PHYXEL_ACCEPTANCE_SCALE='0.25'; $env:PHYXEL_ACCEPTANCE_OPEN_BOUNDARIES='0'
    $env:PHYXEL_MATERIALS_PATH=Join-Path $taskRoot 'empty'; New-Item -ItemType Directory -Force -Path $env:PHYXEL_MATERIALS_PATH | Out-Null
    $env:PHYXEL_CORE_MATERIALS_PATH=$taskCore
    $env:PHYXEL_CONVECTION_RESTART=$RestartPath
    $taskCases=@(@{Fps=$TargetFps;Air=$Air;Hydro=$Hydraulics})
    if(-not $Single -and -not $Baseline) {$taskCases+=@(@{Fps=30;Air=0;Hydro=0},@{Fps=100;Air=0;Hydro=0},@{Fps=60;Air=1;Hydro=1})}
    foreach($taskCase in $taskCases) {
        $taskModes=if($Isolate) {@('water_convection')} elseif($RestartPath) {@('water_convection','water_convection_pause')} else {@('water_convection','water_convection_heated','water_convection_pause')}
        foreach($taskMode in $taskModes) {
            $taskLabel="$taskMode-fps-$($taskCase.Fps)-air-$($taskCase.Air)-hydro-$($taskCase.Hydro)"
            $taskDir=Join-Path $taskRoot $taskLabel; New-Item -ItemType Directory -Force -Path $taskDir | Out-Null
            $env:PHYXEL_ACCEPTANCE_MODE=$taskMode; $env:PHYXEL_ARTIFACT_DIR=$taskDir
            $env:PHYXEL_ACCEPTANCE_TARGET_FPS="$($taskCase.Fps)"; $env:PHYXEL_ACCEPTANCE_AIR="$($taskCase.Air)"
            $env:PHYXEL_ACCEPTANCE_HYDRAULICS="$($taskCase.Hydro)"
            $env:PHYXEL_ACCEPTANCE_CAPTURE_FRAME=if($taskMode -eq 'water_convection_pause') {'60'} else {"$($Seconds * $taskCase.Fps)"}
            $taskProcess=Start-Process -FilePath $taskExe -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $taskDir 'run.log') -RedirectStandardError (Join-Path $taskDir 'error.log')
            Get-Content (Join-Path $taskDir 'run.log') | Where-Object {$_ -match '^PHYXEL_(WATER_CONVECTION|ACCEPTANCE_(SUCCESS|FAILED))'} | Write-Output
            $taskExpected=if($Baseline -and $taskMode -ne 'water_convection_pause') {1} else {0}
            $taskResults.Add([pscustomobject]@{case=$taskLabel;passed=($taskProcess.ExitCode -eq 0);exit=$taskProcess.ExitCode;expectedExit=$taskExpected})
            $taskResults | Export-Csv -LiteralPath (Join-Path $taskRoot 'summary.csv') -NoTypeInformation
            if($taskProcess.ExitCode -ne $taskExpected) { Get-Content (Join-Path $taskDir 'error.log'); throw "Unexpected result $taskLabel" }
        }
    }
} finally {foreach($taskName in $taskNames) {[Environment]::SetEnvironmentVariable($taskName,$taskSaved[$taskName],'Process')}}
