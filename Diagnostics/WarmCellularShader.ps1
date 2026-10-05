param([string]$Runtime=(Join-Path (Split-Path -Parent $PSScriptRoot) 'bin/Debug/net8.0-windows'))
$ErrorActionPreference='Stop'
# Match GpuResourceLifecycleManager.CompileShader's source expansion, flags
# and cache key. The expensive compilation happens before a game window opens.
[void][Reflection.Assembly]::LoadFrom((Join-Path $Runtime 'SharpDX.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $Runtime 'SharpDX.D3DCompiler.dll'))
$taskShaderDir=Join-Path $Runtime 'Content/Shaders'
$taskShaderPath=Join-Path $taskShaderDir 'CellularAutomataSolver.hlsl'
$taskShaderSource=[IO.File]::ReadAllText($taskShaderPath)
foreach($taskInclude in @('PhysicsShared.hlsli','PhaseEnthalpy.hlsli','OxidizerShared.hlsli','FineAirGeometry.hlsli')){
 $taskShaderSource=$taskShaderSource.Replace('#include "'+$taskInclude+'"',[IO.File]::ReadAllText((Join-Path $taskShaderDir $taskInclude)))
}
$taskShaderHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("phyxel-compute-shader-v1`0CSMain`0$taskShaderSource")))
$taskCacheDir=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Phyxel/ShaderCache'
$taskShaderCache=Join-Path $taskCacheDir "$taskShaderHash.cso"
if(Test-Path -LiteralPath $taskShaderCache){Write-Output 'PHYXEL_SHADER_READY cached=True';return}
Write-Output 'PHYXEL_SHADER_PREPARING CellularAutomataSolver (before opening test window)'
$taskCompilation=[SharpDX.D3DCompiler.ShaderBytecode]::Compile($taskShaderSource,'CSMain','cs_5_0',[SharpDX.D3DCompiler.ShaderFlags]::OptimizationLevel3,[SharpDX.D3DCompiler.EffectFlags]::None,$null,$null,$taskShaderPath)
try {
 New-Item -ItemType Directory -Force -Path $taskCacheDir|Out-Null
 [IO.File]::WriteAllBytes($taskShaderCache,$taskCompilation.Bytecode.Data)
 Write-Output "PHYXEL_SHADER_READY cached=False bytes=$($taskCompilation.Bytecode.Data.Length)"
}finally{$taskCompilation.Dispose()}
