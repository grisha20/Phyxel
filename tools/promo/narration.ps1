param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$taskVoice = New-Object -ComObject SAPI.SpVoice
$taskRussian = @($taskVoice.GetVoices() | Where-Object { $_.GetDescription() -match 'Irina.*Russian' })
if ($taskRussian.Count -eq 0) { throw 'Microsoft Irina Desktop Russian voice is required for this draft.' }
$taskVoice.Voice = $taskRussian[0]
$taskVoice.Rate = 1
$taskVoice.Volume = 100
$taskLines = @(
    'Я начал делать свою игру с воды.',
    'Потом добавил огонь и нагрев.',
    'Выбираешь материалы.',
    'Собрал печку. Здесь даже видно движение газов.',
    'А потом решил проверить одну бомбу.',
    'Какой эксперимент сделать следующим?'
)
for ($taskIndex=0; $taskIndex -lt $taskLines.Count; $taskIndex++) {
    $taskStream = New-Object -ComObject SAPI.SpFileStream
    $taskStream.Format.Type = 22
    $taskDestination = Join-Path $OutputDirectory ('voice_{0:00}.wav' -f $taskIndex)
    $taskStream.Open($taskDestination, 3, $false)
    $taskVoice.AudioOutputStream = $taskStream
    [void]$taskVoice.Speak($taskLines[$taskIndex])
    $taskStream.Close()
}
Write-Output 'Created six Russian narration clips using the installed Windows voice.'
