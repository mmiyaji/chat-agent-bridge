param([Parameter(Mandatory)][string]$DataDir,[ValidateSet('ja','en')][string]$Language='ja',[switch]$SeedManualOverride)
$ErrorActionPreference='Stop'
$fixturePath=[IO.Path]::GetFullPath($DataDir)
& (Join-Path $PSScriptRoot 'New-UsageActivityFixture.ps1') -DataDir $fixturePath -Language $Language -CostFixture|Out-Null
$catalogPath=Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json'
$catalog=Get-Content -LiteralPath $catalogPath -Raw
$now=[DateTimeOffset]::UtcNow
@{schemaVersion=1;autoUpdateEnabled=$false;lastCheckedUtc=$now.ToString('o');lastUpdatedUtc=$now.ToString('o');etag=$null;catalogJson=$catalog}|ConvertTo-Json -Depth 10|Set-Content (Join-Path $fixturePath 'pricing-auto.json') -Encoding utf8
$manualModels=@()
if($SeedManualOverride){
    $manualModels=@(@{model='gpt-5.3-codex';input=@{min=4;max=4};cachedInput=@{min=0.4;max=0.4};cacheWrite=@{min=5;max=5};output=@{min=20;max=20}})
}
@{schemaVersion=1;models=$manualModels}|ConvertTo-Json -Depth 10|Set-Content (Join-Path $fixturePath 'pricing-manual.json') -Encoding utf8
New-Item -ItemType File (Join-Path $fixturePath '.cab-pricing-settings-test')|Out-Null
Write-Output $fixturePath
