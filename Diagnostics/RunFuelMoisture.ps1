param([string]$ArtifactRoot='artifacts/fuel-moisture', [string]$RuntimeDirectory='bin/Debug/net8.0-windows',
    [string]$CoreMaterialsPath='', [switch]$ExpectFailure, [string]$LegacyContactShader='')
$ErrorActionPreference='Stop'
$taskRepository=Split-Path -Parent $PSScriptRoot
$taskDirectory=[IO.Path]::GetFullPath((Join-Path $taskRepository $ArtifactRoot))
$taskVariables=@('PHYXEL_VERIFY_FUEL_MOISTURE','PHYXEL_ARTIFACT_DIR','PHYXEL_WINDOWED',
    'PHYXEL_CORE_MATERIALS_PATH','PHYXEL_MATERIALS_PATH','PHYXEL_ACCEPTANCE_MODE','PHYXEL_MOISTURE_LEGACY_SHADER')
$taskPrevious=@{}
foreach($taskName in $taskVariables) { $taskPrevious[$taskName]=[Environment]::GetEnvironmentVariable($taskName,'Process') }
try {
    New-Item -ItemType Directory -Force -Path $taskDirectory | Out-Null
    foreach($taskName in @('PHYXEL_CORE_MATERIALS_PATH','PHYXEL_MATERIALS_PATH','PHYXEL_ACCEPTANCE_MODE')) {
        Remove-Item "Env:$taskName" -ErrorAction SilentlyContinue
    }
    if($CoreMaterialsPath) {
        $env:PHYXEL_CORE_MATERIALS_PATH=[IO.Path]::GetFullPath((Join-Path $taskRepository $CoreMaterialsPath))
        $taskExternal=Join-Path $taskDirectory 'empty-external'
        New-Item -ItemType Directory -Force -Path $taskExternal | Out-Null
        $env:PHYXEL_MATERIALS_PATH=$taskExternal
    }
    $env:PHYXEL_VERIFY_FUEL_MOISTURE='1'
    $env:PHYXEL_MOISTURE_LEGACY_SHADER=if($LegacyContactShader){[IO.Path]::GetFullPath((Join-Path $taskRepository $LegacyContactShader))}else{$null}
    $env:PHYXEL_WINDOWED='1'
    $env:PHYXEL_ARTIFACT_DIR=$taskDirectory
    $taskExecutable=Join-Path $taskRepository "$RuntimeDirectory/Phyxel.exe"
    $taskProcess=Start-Process -FilePath $taskExecutable -WorkingDirectory $taskRepository -WindowStyle Hidden -Wait -PassThru `
        -RedirectStandardOutput (Join-Path $taskDirectory 'run.log') `
        -RedirectStandardError (Join-Path $taskDirectory 'error.log')
    $taskOutput=Get-Content (Join-Path $taskDirectory 'run.log')
    $taskOutput | Where-Object { $_ -like 'PHYXEL_FUEL_MOISTURE_*' -and $_ -notlike '*CHECK_FAILED*' } | Write-Output
    if($ExpectFailure) {
        if($taskProcess.ExitCode -eq 0 -or -not ($taskOutput -match '^PHYXEL_FUEL_MOISTURE_RESULT passed=False ') -or
            -not ($taskOutput -match '^PHYXEL_FUEL_MOISTURE_CHECK_FAILED core:coal conserved uptake/latch')) {
            throw 'Expected missing conserved moisture was not reproduced.'
        }
    } elseif($taskProcess.ExitCode -ne 0 -or -not ($taskOutput -match '^PHYXEL_FUEL_MOISTURE_RESULT passed=True ')) {
        Get-Content (Join-Path $taskDirectory 'error.log') | Write-Output
        throw 'Fuel moisture GPU checks failed.'
    }
} finally {
    foreach($taskName in $taskVariables) { [Environment]::SetEnvironmentVariable($taskName,$taskPrevious[$taskName],'Process') }
}
