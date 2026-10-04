param([string]$Configuration='Debug', [string]$ArtifactRoot='artifacts/metal-fusion',
    [string]$CoreMaterialsPath='', [switch]$ExpectFailure)
$ErrorActionPreference='Stop'
$taskRepository=Split-Path -Parent $PSScriptRoot
$taskExecutable=Join-Path $taskRepository "bin/$Configuration/net8.0-windows/Phyxel.exe"
$taskDirectory=[IO.Path]::GetFullPath((Join-Path $taskRepository $ArtifactRoot))
$taskVariables=@('PHYXEL_VERIFY_METAL_FUSION','PHYXEL_VERIFY_ICE_FUSION','PHYXEL_WINDOWED','PHYXEL_ARTIFACT_DIR',
    'PHYXEL_ACCEPTANCE_MODE','PHYXEL_CORE_MATERIALS_PATH','PHYXEL_MATERIALS_PATH')
$taskPrevious=@{}
foreach($taskName in $taskVariables) { $taskPrevious[$taskName]=[Environment]::GetEnvironmentVariable($taskName,'Process') }
try {
    New-Item -ItemType Directory -Force -Path $taskDirectory | Out-Null
    foreach($taskName in @('PHYXEL_VERIFY_ICE_FUSION','PHYXEL_ACCEPTANCE_MODE','PHYXEL_CORE_MATERIALS_PATH','PHYXEL_MATERIALS_PATH')) {
        Remove-Item "Env:$taskName" -ErrorAction SilentlyContinue
    }
    $env:PHYXEL_VERIFY_METAL_FUSION='1'
    if($CoreMaterialsPath) {
        $env:PHYXEL_CORE_MATERIALS_PATH=[IO.Path]::GetFullPath((Join-Path $taskRepository $CoreMaterialsPath))
        $taskExternal=Join-Path $taskDirectory 'empty-external'
        New-Item -ItemType Directory -Force -Path $taskExternal | Out-Null
        $env:PHYXEL_MATERIALS_PATH=$taskExternal
    }
    $env:PHYXEL_WINDOWED='1'
    $env:PHYXEL_ARTIFACT_DIR=$taskDirectory
    $taskProcess=Start-Process -FilePath $taskExecutable -WorkingDirectory $taskRepository -WindowStyle Hidden -Wait -PassThru `
        -RedirectStandardOutput (Join-Path $taskDirectory 'run.log') `
        -RedirectStandardError (Join-Path $taskDirectory 'error.log')
    $taskOutput=Get-Content (Join-Path $taskDirectory 'run.log')
    $taskOutput | Where-Object { $_ -like 'PHYXEL_METAL_*' } | Write-Output
    if($ExpectFailure) {
        if($taskProcess.ExitCode -eq 0 -or -not ($taskOutput -match '^PHYXEL_METAL_FUSION_RESULT passed=False ') -or
            -not ($taskOutput -match '^PHYXEL_METAL_CHECK_FAILED partial-melt energy ')) {
            throw 'Expected baseline energy defect was not reproduced.'
        }
    } elseif($taskProcess.ExitCode -ne 0 -or -not ($taskOutput -match '^PHYXEL_METAL_FUSION_RESULT passed=True ')) {
        Get-Content (Join-Path $taskDirectory 'error.log') | Write-Output
        throw 'Metal fusion GPU checks failed.'
    }
} finally {
    foreach($taskName in $taskVariables) { [Environment]::SetEnvironmentVariable($taskName,$taskPrevious[$taskName],'Process') }
}
