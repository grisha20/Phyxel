param([switch]$NonInteractive,[ValidateRange(10,60)][int]$Seconds=15,[double[]]$Scales=@(.5,1))
$ErrorActionPreference='Stop'
if(Get-Process Phyxel -ErrorAction SilentlyContinue){throw 'Save your scene and close Phyxel before profiling.'}
$taskExe=Join-Path $PSScriptRoot 'Phyxel.exe'
if(!(Test-Path -LiteralPath $taskExe)){throw 'Place Profile-Coal.ps1 beside Phyxel.exe.'}
foreach($taskScale in $Scales){if($taskScale -lt .25 -or $taskScale -gt 1){throw 'Scale must be .25 to 1.'}}
$taskRoot=Join-Path $env:LOCALAPPDATA ('Phyxel\Benchmarks\Coal-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+$PID)
New-Item -ItemType Directory -Path $taskRoot|Out-Null
$taskSaved=@{};foreach($taskVar in Get-ChildItem Env:PHYXEL_*){$taskSaved[$taskVar.Name]=$taskVar.Value}
$taskExit=0
try {
    foreach($taskVar in Get-ChildItem Env:PHYXEL_*){Remove-Item -LiteralPath ('Env:'+$taskVar.Name)}
    $taskDir=Join-Path $taskRoot 'kernels';New-Item -ItemType Directory -Path $taskDir|Out-Null
    $env:PHYXEL_VERIFY_COAL_GPU='1';$env:PHYXEL_WINDOWED='1';$env:PHYXEL_ARTIFACT_DIR=$taskDir
    Write-Host 'Checking exact GPU results and smoke display. Then measuring both algorithms on this GPU.'
    $taskProcess=Start-Process $taskExe -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/error.log"
    if($taskProcess.ExitCode -ne 0 -or -not(Select-String -LiteralPath "$taskDir/run.log" -Pattern '^PHYXEL_COAL_GPU_SUCCESS')){throw 'GPU regression checks failed.'}
    Remove-Item Env:PHYXEL_VERIFY_COAL_GPU
    $taskRows=@()
    foreach($taskScale in $Scales){foreach($taskReference in $true,$false){
        $taskLabel=if($taskReference){'reference'}else{'current'}
        $taskDir=Join-Path $taskRoot ($taskScale.ToString([Globalization.CultureInfo]::InvariantCulture)+'-'+$taskLabel)
        Write-Host "Coal strip: scale=$taskScale algorithm=$taskLabel (Simulation and Sandbox)"
        & "$PSScriptRoot/Diagnostics/RunFirePerformance.ps1" -ArtifactRoot $taskDir -Seconds $Seconds -Scale $taskScale -CoalStrip -OxygenReference:$taskReference -Executable 'Phyxel.exe'
        foreach($taskRow in Import-Csv "$taskDir/summary.csv"){
            $taskRow|Add-Member -NotePropertyName Algorithm -NotePropertyValue $taskLabel
            $taskRows+=$taskRow
        }
    }}
    @{executableSha256=(Get-FileHash -LiteralPath $taskExe).Hash;seconds=$Seconds;rows=$taskRows}|ConvertTo-Json -Depth 5|Set-Content "$taskRoot/measurements.json"
    Write-Host "Results: $taskRoot"
    Write-Host 'Share this whole Coal folder: measurements.json, clock.csv, frame.csv and run.log files.'
} catch {Write-Host $_;$taskExit=1;Write-Host "Results: $taskRoot"}
finally {
    foreach($taskVar in Get-ChildItem Env:PHYXEL_*){Remove-Item -LiteralPath ('Env:'+$taskVar.Name)}
    foreach($taskName in $taskSaved.Keys){[Environment]::SetEnvironmentVariable($taskName,$taskSaved[$taskName],'Process')}
}
if(!$NonInteractive){Read-Host 'Press Enter'}
exit $taskExit
