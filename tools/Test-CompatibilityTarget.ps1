param(
    [Parameter(Mandatory = $true)][string]$Name,
    [Parameter(Mandatory = $true)][string]$GameRoot,
    [Parameter(Mandatory = $true)][string]$TypeName,
    [Parameter(Mandatory = $true)][string]$CurrentNames,
    [Parameter(Mandatory = $true)][string]$MapNames,
    [Parameter(Mandatory = $true)][string]$UpdateNames
)

$ErrorActionPreference = 'Stop'
$assemblyPath = Join-Path $GameRoot 'EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll'
if (-not (Test-Path -LiteralPath $assemblyPath)) {
    throw "[$Name] Missing Assembly-CSharp.dll: $assemblyPath"
}

$assembly = [Reflection.Assembly]::LoadFrom($assemblyPath)
$type = $assembly.GetType($TypeName, $false)
if ($null -eq $type) {
    throw "[$Name] Missing type: $TypeName"
}

$flags = [Reflection.BindingFlags]'Instance,Static,Public,NonPublic'
$memberNames = @($type.GetProperties($flags).Name) + @($type.GetFields($flags).Name)
$methodNames = @($type.GetMethods($flags).Name)

$currentCandidates = $CurrentNames.Split(',')
$mapCandidates = $MapNames.Split(',')
$updateCandidates = $UpdateNames.Split(',')

if (-not ($currentCandidates | Where-Object { $memberNames -ccontains $_ })) {
    throw "[$Name] Missing current-language member"
}
if (-not ($mapCandidates | Where-Object { $memberNames -ccontains $_ })) {
    throw "[$Name] Missing locale-font map"
}
if (-not ($updateCandidates | Where-Object { $methodNames -ccontains $_ })) {
    throw "[$Name] Missing font update method"
}

Write-Host "[$Name] LocaleManager contract OK" -ForegroundColor Green
