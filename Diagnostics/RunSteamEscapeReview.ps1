param(
    [Parameter(Mandatory=$true)][string]$LargeScene,
    [Parameter(Mandatory=$true)][string]$SmallScene,
    [Parameter(Mandatory=$true)][string]$DryScene,
    [string]$Configuration='Debug',
    [string]$ArtifactRoot='artifacts/steam-escape-review'
)
# Run after building. Only replay copies; never modify the supplied scenes.
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskRoot=if([IO.Path]::IsPathRooted($ArtifactRoot)){[IO.Path]::GetFullPath($ArtifactRoot)}else{[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))}
$taskSources=@($LargeScene,$SmallScene,$DryScene) | ForEach-Object { [IO.Path]::GetFullPath($_) }
$taskExe=Join-Path $taskRepo "bin/$Configuration/net8.0-windows/Phyxel.exe"
if(Get-Process Phyxel -ErrorAction SilentlyContinue) { throw 'Close Phyxel before a measured replay.' }
if(-not(Test-Path -LiteralPath $taskExe)) { throw 'Build Phyxel before running the review.' }
New-Item -ItemType Directory -Force $taskRoot | Out-Null
$taskSaved=@{}
foreach($taskVariable in Get-ChildItem Env:PHYXEL_*) { $taskSaved[$taskVariable.Name]=$taskVariable.Value }
function Invoke-SteamCase([string]$Name,[hashtable]$Variables) {
    foreach($taskVariable in Get-ChildItem Env:PHYXEL_*) { [Environment]::SetEnvironmentVariable($taskVariable.Name,$null,'Process') }
    $taskDirectory=Join-Path $taskRoot $Name
    New-Item -ItemType Directory -Force $taskDirectory | Out-Null
    $env:PHYXEL_ARTIFACT_DIR=$taskDirectory
    $env:PHYXEL_WINDOWED='1'
    foreach($taskKey in $Variables.Keys) { [Environment]::SetEnvironmentVariable($taskKey,[string]$Variables[$taskKey],'Process') }
    $taskProcess=Start-Process $taskExe -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru `
        -RedirectStandardOutput (Join-Path $taskDirectory 'run.log') -RedirectStandardError (Join-Path $taskDirectory 'errors.log')
    if($taskProcess.ExitCode -ne 0) { Get-Content (Join-Path $taskDirectory 'errors.log');throw "Steam case failed: $Name" }
    # Exit zero verifies replay/save-load checks, not the quantitative SE criteria.
    Write-Output "STEAM_ESCAPE_REPLAY_PASS $Name"
}
try {
    Invoke-SteamCase 'controls' @{PHYXEL_VERIFY_WATER_QUENCH=1}
    foreach($taskScene in @(@('large',$LargeScene),@('small',$SmallScene),@('dry',$DryScene))) {
        $taskPath=Join-Path $taskRoot ($taskScene[0]+'.json')
        if($taskSources -contains $taskPath) { throw 'Artifact path must differ from every source scene.' }
        Copy-Item -LiteralPath $taskScene[1] -Destination $taskPath -Force
        Copy-Item -LiteralPath ([IO.Path]::ChangeExtension($taskScene[1],'.world')) -Destination ([IO.Path]::ChangeExtension($taskPath,'.world')) -Force
        foreach($taskMode in @('Simulation','Sandbox')) {
            foreach($taskFps in @(30,60,100)) {
                if($taskScene[0] -eq 'dry' -and ($taskMode -ne 'Simulation' -or $taskFps -ne 60)) { continue }
                Invoke-SteamCase "$($taskScene[0])-$taskMode-$taskFps" @{
                    PHYXEL_VERIFY_FURNACE_SENSORS=1;PHYXEL_SENSOR_SCENE=$taskPath;PHYXEL_SENSOR_MODE=$taskMode
                    PHYXEL_SENSOR_FPS=$taskFps;PHYXEL_SENSOR_SECONDS=30;PHYXEL_SENSOR_EARLY=1
                }
            }
        }
    }
} finally {
    foreach($taskVariable in Get-ChildItem Env:PHYXEL_*) { [Environment]::SetEnvironmentVariable($taskVariable.Name,$null,'Process') }
    foreach($taskKey in $taskSaved.Keys) { [Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey],'Process') }
}
