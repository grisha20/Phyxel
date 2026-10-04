param([string]$ArtifactRoot='artifacts/combustion-modes-20261002',[ValidateSet(0,1)][int]$Air=0)
$ErrorActionPreference='Stop'
$taskModeBefore=$env:PHYXEL_ACCEPTANCE_SIMULATION_MODE
$taskSwitchBefore=$env:PHYXEL_OXIDIZER_SWITCH_MODES
try {
    $env:PHYXEL_OXIDIZER_SWITCH_MODES=$null
    $env:PHYXEL_ACCEPTANCE_SIMULATION_MODE='simulation'
    & "$PSScriptRoot/RunOxidizer.ps1" -ArtifactRoot "$ArtifactRoot/simulation" -Single -SingleAir $Air
    $env:PHYXEL_ACCEPTANCE_SIMULATION_MODE='sandbox'
    & "$PSScriptRoot/RunOxidizer.ps1" -ArtifactRoot "$ArtifactRoot/sandbox" -Single -SingleAir $Air
    $env:PHYXEL_OXIDIZER_SWITCH_MODES='1'
    & "$PSScriptRoot/RunOxidizer.ps1" -ArtifactRoot "$ArtifactRoot/switch" -Single -SingleAir $Air
} finally {
    $env:PHYXEL_ACCEPTANCE_SIMULATION_MODE=$taskModeBefore
    $env:PHYXEL_OXIDIZER_SWITCH_MODES=$taskSwitchBefore
}
