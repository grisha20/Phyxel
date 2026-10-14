param([switch]$Apply)
$ErrorActionPreference='Stop'

# Only disposable outputs inside this checkout's artifacts directory are eligible.
# Preview is the default. User saves, source/fixture copies, code and reports survive.
$taskRepo=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\','/')
$taskRoot=[IO.Path]::GetFullPath((Join-Path $taskRepo 'artifacts')).TrimEnd('\','/')
if(!(Test-Path -LiteralPath $taskRoot -PathType Container)){Write-Output 'No artifacts directory.';exit 0}
if((Get-Item -LiteralPath $taskRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Refusing a linked artifacts root.'}
if($Apply -and (Get-Process Phyxel -ErrorAction SilentlyContinue)){throw 'Close Phyxel before cleaning diagnostic outputs.'}
$taskPrefix=$taskRoot+[IO.Path]::DirectorySeparatorChar
function Assert-ArtifactPath([string]$Path){
 $taskResolved=[IO.Path]::GetFullPath($Path)
 if(!$taskResolved.StartsWith($taskPrefix,[StringComparison]::OrdinalIgnoreCase)){throw "Path escapes artifacts: $Path"}
 return $taskResolved
}
$taskTracked=@(& git -C $taskRepo ls-files -- artifacts)
if($LASTEXITCODE -ne 0){throw 'Cannot check tracked files.'}
if($taskTracked.Count -gt 0){throw 'Tracked artifacts exist; inspect them before cleanup.'}
$taskFiles=[Collections.Generic.List[IO.FileInfo]]::new()
$taskDirectories=[Collections.Generic.List[IO.DirectoryInfo]]::new()
$taskStack=[Collections.Generic.Stack[string]]::new();$taskStack.Push($taskRoot)
while($taskStack.Count){
 foreach($taskEntry in Get-ChildItem -LiteralPath $taskStack.Pop() -Force){
  $null=Assert-ArtifactPath $taskEntry.FullName
  if($taskEntry.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Linked entry requires inspection: $($taskEntry.FullName)"}
  if($taskEntry.PSIsContainer){$taskDirectories.Add($taskEntry);$taskStack.Push($taskEntry.FullName)}
  else{$taskFiles.Add($taskEntry)}
 }
}
function Is-Protected([IO.FileInfo]$File){
 # Every file was already checked against the canonical root prefix.
 $taskRelative=$File.FullName.Substring($taskPrefix.Length)
 return $taskRelative -match '(^|[\\/])(source|original|input|inputs|fixture|fixtures)([\\/]|$)' -or
  $taskRelative -match '^(material-card-images|filter-art|filter-wide-art|filter-card-layout)' -or
  $File.BaseName -match '^(source|original|input)([._-]|$)'
}
$taskGeneratedScenes=@(
 'brush-scene','bulk-phase','cast_iron','co2','coal-mound','cold-ring','copper','dry','during-pulse',
 'empty.scene','energy.scene','filter-only','final.scene','finite-heat','floating-partial',
 'fragment-roundtrip','gas','heated-air','hot-wet-v12','jet-off','jet-roundtrip','large',
 'mid-reaction','moisture','neutral-cold','oil-cold','oil-feed-demo','oil-hot','oil-on-water-demo',
 'oily-wood','partial-boiling','partial-fusion','partial-metal','partial-oil','partial-wetting','partial',
 'plastic-roundtrip','pool-reload','power-off','probe','radiant-phase','recovered-grid','reset-flow',
 'right-inlet','rotated','roundtrip-scene','roundtrip','scene','single-plume','steam-apparatus.scene',
 'steam-cycle.scene','steam','steel','two-plumes','undo-save','vapour-cushion','warm-empty',
 'water-cold','water-hot','water-ice-steam-v6','water-ice-steam-v7','water-ice-steam',
 'wet-wood','wet','wick-half','without-external','Первая сцена','Вторая сцена','Расширенная сцена'
)
$taskCandidates=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
function Add-Candidate([IO.FileInfo]$File,[string]$Reason){
 $taskCandidates[$File.FullName]=[PSCustomObject]@{Path=$File.FullName;Bytes=$File.Length;LastWriteUtc=$File.LastWriteTimeUtc.ToString('o');Reason=$Reason}
}
$taskImageGroups=@{}
foreach($taskFile in $taskFiles){
 if(Is-Protected $taskFile){continue}
 $taskExtension=$taskFile.Extension.ToLowerInvariant()
 if($taskExtension -in '.bin','.grid','.npz','.npy'){Add-Candidate $taskFile 'raw diagnostic state';continue}
 if($taskExtension -in '.dll','.exe','.pdb','.cso','.sha256','.xnb','.zip','.pyc'){
  Add-Candidate $taskFile 'obsolete diagnostic runtime/package/cache';continue
 }
 if($taskExtension -eq '.world' -and ($taskFile.BaseName -in $taskGeneratedScenes -or
  $taskFile.BaseName -match '^(snapshot-\d+|(?:continued-)?(?:Simulation|Sandbox)-\d+-active|continued-(?:Simulation|Sandbox)-\d+|front-(?:Simulation|Sandbox)|(?:fuel|furnace|tnt|oil|shell|released|performance|legacy|parcel)-.+)$')){
  Add-Candidate $taskFile 'generated regression save';continue
 }
 if($taskExtension -eq '.png' -and $taskFile.BaseName -match '^(?<prefix>.*?)(?<index>\d+)(?<suffix>s|-air|-plain|-effects)?$'){
  $taskKey=$taskFile.DirectoryName+'|'+$Matches.prefix+'|'+$Matches.suffix
  if(!$taskImageGroups.ContainsKey($taskKey)){$taskImageGroups[$taskKey]=[Collections.Generic.List[object]]::new()}
  $taskImageGroups[$taskKey].Add([PSCustomObject]@{File=$taskFile;Index=[long]$Matches.index})
 }
}
foreach($taskGroup in $taskImageGroups.Values){
 if($taskGroup.Count -le 2){continue}
 $taskOrdered=@($taskGroup|Sort-Object Index)
 for($taskIndex=1;$taskIndex -lt $taskOrdered.Count-1;$taskIndex++){
  Add-Candidate $taskOrdered[$taskIndex].File 'intermediate screenshot; first/last retained'
 }
}
$taskRows=@($taskCandidates.Values|Sort-Object Path)
$taskBytes=($taskRows|Measure-Object Bytes -Sum).Sum
$taskSummary=@($taskRows|Group-Object Reason|ForEach-Object{
 [PSCustomObject]@{Reason=$_.Name;Files=$_.Count;GiB=[math]::Round((($_.Group|Measure-Object Bytes -Sum).Sum/1GB),3)}
})
$taskSummary|Format-Table -AutoSize
Write-Output ("{0}: {1} files, {2:N3} GiB" -f $(if($Apply){'Removing'}else{'Preview only'}),$taskRows.Count,($taskBytes/1GB))
if(!$Apply -or !$taskRows.Count){exit 0}
$taskAudit=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('PhyxelDiagnostics/cleanup-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $taskAudit -Force|Out-Null
$taskRows|Export-Csv -LiteralPath (Join-Path $taskAudit 'removed-files.csv') -NoTypeInformation -Encoding utf8
[long]$taskRemovedBytes=0;[int]$taskRemovedCount=0
foreach($taskRow in $taskRows){
 $taskPath=Assert-ArtifactPath $taskRow.Path
 $taskCurrent=Get-Item -LiteralPath $taskPath -Force
 if($taskCurrent.PSIsContainer -or ($taskCurrent.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
  $taskCurrent.Length -ne $taskRow.Bytes -or $taskCurrent.LastWriteTimeUtc.ToString('o') -ne $taskRow.LastWriteUtc){
  throw "Artifact changed after preview: $taskPath"
 }
 Remove-Item -LiteralPath $taskPath -Force
 $taskRemovedBytes+=$taskRow.Bytes;$taskRemovedCount++
}
# Only empty ordinary directories; no recursive directory deletion.
foreach($taskDirectory in ($taskDirectories|Sort-Object {$_.FullName.Length} -Descending)){
 $taskPath=Assert-ArtifactPath $taskDirectory.FullName
 if((Test-Path -LiteralPath $taskPath) -and !(Get-ChildItem -LiteralPath $taskPath -Force|Select-Object -First 1)){
  Remove-Item -LiteralPath $taskPath
 }
}
[PSCustomObject]@{Files=$taskRemovedCount;Bytes=$taskRemovedBytes;GiB=$taskRemovedBytes/1GB;Root=$taskRoot;Manifest=(Join-Path $taskAudit 'removed-files.csv')}|
 ConvertTo-Json|Tee-Object -FilePath (Join-Path $taskAudit 'summary.json')
