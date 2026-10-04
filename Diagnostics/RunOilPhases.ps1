param([string]$ArtifactRoot='artifacts/oil-phases')
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskRoot=[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))
New-Item -ItemType Directory -Force $taskRoot | Out-Null
$taskPrevious=@{}
Get-ChildItem Env:PHYXEL* | ForEach-Object {$taskPrevious[$_.Name]=$_.Value;Remove-Item -LiteralPath "Env:$($_.Name)"}
try {
 $env:PHYXEL_VERIFY_OIL_PHASES='1';$env:PHYXEL_WINDOWED='1';$env:PHYXEL_ARTIFACT_DIR=$taskRoot
 $taskP=Start-Process (Join-Path $taskRepo 'bin/Debug/net8.0-windows/Phyxel.exe') -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskRoot/run.log" -RedirectStandardError "$taskRoot/error.log"
 Get-Content "$taskRoot/run.log" | Where-Object {$_ -match '^PHYXEL_(OP_|OIL_PHASES)'}
 if($taskP.ExitCode -ne 0){Get-Content "$taskRoot/error.log";throw 'Oil phase checks failed'}
} finally {
 Get-ChildItem Env:PHYXEL* | ForEach-Object {Remove-Item -LiteralPath "Env:$($_.Name)"}
 foreach($taskName in $taskPrevious.Keys){[Environment]::SetEnvironmentVariable($taskName,$taskPrevious[$taskName])}
}
