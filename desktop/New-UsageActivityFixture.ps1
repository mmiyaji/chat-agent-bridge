param([Parameter(Mandatory)][string]$DataDir,[ValidateSet('ja','en')][string]$Language='ja')
$ErrorActionPreference='Stop'
$fixturePath=[IO.Path]::GetFullPath($DataDir)
$artifactRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))+[IO.Path]::DirectorySeparatorChar
if(-not $fixturePath.StartsWith($artifactRoot,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $fixturePath)){
    throw 'Choose a new directory under desktop/artifacts. Existing directories are never overwritten.'
}
New-Item -ItemType Directory -Path $fixturePath|Out-Null
$records=[Collections.Generic.List[object]]::new()
function Add-Run([int]$Day,$Usage){
    $records.Add(@{Id=[Guid]::NewGuid().ToString();StartedAt=[DateTimeOffset]::new([DateTime]::Today.AddDays(-$Day).AddHours(9)).ToString('o');Status='completed';Source='local';WorkspaceRoot=$fixturePath;Usage=$Usage})
}
function Add-Usage([int]$Day,[string]$Scope,$Model,$InputCount,$OutputCount,$ReadCount,$WriteCount=0,[string]$Source='fixture',[bool]$IncludesCache=$true){
    Add-Run $Day @{Scope=$Scope;Model=$Model;InputTokens=$InputCount;OutputTokens=$OutputCount;CachedInputTokens=$ReadCount;CacheWriteInputTokens=$WriteCount;Source=$Source;InputIncludesCache=$IncludesCache}
}
# 374 runs in 90 days, 493 retained overall; 25 complete turn usage reports.
for($i=0;$i -lt 345;$i++){Add-Run ($i%90) $null}
for($i=0;$i -lt 119;$i++){Add-Run (90+$i%30) $null}
Add-Usage 0 'turn' 'model-a' 100 10 100
Add-Usage 0 'turn' 'model-a' 900 20 0
Add-Usage 1 'thread-total' 'model-b' 20 5 60 20 'claude.stream-json' $false
Add-Usage 2 'last-request' 'model-c' 200 20 150
Add-Usage 3 'thread-total' 'model-d' 100000 500 90000
Add-Usage 4 'turn' $null 0 0 0
Add-Usage 5 'turn' 'model-a' 20 $null $null
Add-Usage 6 'turn' 'model-b' $null 10 $null
for($day=7;$day -lt 28;$day++){
    Add-Usage $day 'turn' $(if($day%2){'model-a'}else{'model-b'}) ($day*100) ($day*10) ($day*35)
}
@{SchemaVersion=1;Records=$records.ToArray()}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $fixturePath 'history.json') -Encoding utf8
@{SchemaVersion=1;WorkspaceRoot=$fixturePath;AgentExecutable='claude';AgentProvider='claude';AutoConnectProvider='none'}|ConvertTo-Json|Set-Content (Join-Path $fixturePath 'settings.json') -Encoding utf8
@{Language=$Language}|ConvertTo-Json|Set-Content (Join-Path $fixturePath 'language.json') -Encoding utf8
@{Theme='dark'}|ConvertTo-Json|Set-Content (Join-Path $fixturePath 'appearance.json') -Encoding utf8
New-Item -ItemType File (Join-Path $fixturePath '.cab-activity-test')|Out-Null
Write-Output $fixturePath
