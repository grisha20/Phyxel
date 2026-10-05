param([string]$Runtime=(Join-Path (Split-Path -Parent $PSScriptRoot) 'bin/Debug/net8.0-windows'),[switch]$SkipCellular)
$ErrorActionPreference='Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $Runtime 'SharpDX.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $Runtime 'SharpDX.D3DCompiler.dll'))
$taskDir=Join-Path $Runtime 'Content/Shaders'
$taskCache=Join-Path $env:LOCALAPPDATA 'Phyxel/ShaderCache'
New-Item -ItemType Directory -Force $taskCache|Out-Null
$taskMatches=[regex]::Matches([IO.File]::ReadAllText((Join-Path (Split-Path -Parent $PSScriptRoot) 'Graphics/GpuResourceLifecycleManager.cs')),'CompileShader\("([^"]+)"(?:,\s*"([^"]+)")?\)')
$taskCount=0
foreach($taskMatch in $taskMatches){
 $taskFile=$taskMatch.Groups[1].Value
 if($SkipCellular -and $taskFile -eq 'CellularAutomataSolver.hlsl'){continue}
 $taskEntry=$taskMatch.Groups[2].Value;if(!$taskEntry){$taskEntry='CSMain'}
 $taskSource=[IO.File]::ReadAllText((Join-Path $taskDir $taskFile))
 foreach($taskInclude in @('PhysicsShared.hlsli','PhaseEnthalpy.hlsli','OxidizerShared.hlsli','FineAirGeometry.hlsli')){
  $taskSource=$taskSource.Replace('#include "'+$taskInclude+'"',[IO.File]::ReadAllText((Join-Path $taskDir $taskInclude)))
 }
 $taskHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("phyxel-compute-shader-v1`0$taskEntry`0$taskSource")))
 $taskPath=Join-Path $taskCache "$taskHash.cso"
 if(!(Test-Path -LiteralPath $taskPath)){
  Write-Output "PHYXEL_SHADER_PREPARING $taskFile $taskEntry"
  $taskC=[SharpDX.D3DCompiler.ShaderBytecode]::Compile($taskSource,$taskEntry,'cs_5_0',[SharpDX.D3DCompiler.ShaderFlags]::OptimizationLevel3,[SharpDX.D3DCompiler.EffectFlags]::None,$null,$null,(Join-Path $taskDir $taskFile))
  try{[IO.File]::WriteAllBytes($taskPath,$taskC.Bytecode.Data)}finally{$taskC.Dispose()}
 }
 $taskCount++
}
Write-Output "PHYXEL_SHADER_MATRIX_COMPLETE entries=$taskCount"
