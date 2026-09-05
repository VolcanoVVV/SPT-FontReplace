param(
    [string]$Game311 = 'V:\EFToffline\EFToffline - 3.11.0debug',
    [string]$Game40 = 'V:\EFToffline\EFToffline - 4.0debug',
    [string]$Game41 = 'V:\EFToffline\EFToffline - 4.1debug'
)

$ErrorActionPreference = 'Stop'
$checker = Join-Path $PSScriptRoot 'Test-CompatibilityTarget.ps1'
$targets = @(
    @{ Name = '3.11'; Root = $Game311; Type = 'LocaleManagerClass'; Current = 'String_0'; Map = 'dictionary_1,Dictionary_1'; Update = 'method_1' },
    @{ Name = '4.0'; Root = $Game40; Type = 'LocaleManagerClass'; Current = 'String_0'; Map = 'dictionary_1,Dictionary_1'; Update = 'method_1' },
    @{ Name = '4.1'; Root = $Game41; Type = 'EFT.LocalizationManager'; Current = 'Culture'; Map = '_languageSpecificFallBacks'; Update = 'UpdateFonts' }
)

foreach ($target in $targets) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $checker `
        -Name $target.Name `
        -GameRoot $target.Root `
        -TypeName $target.Type `
        -CurrentNames $target.Current `
        -MapNames $target.Map `
        -UpdateNames $target.Update

    if ($LASTEXITCODE -ne 0) {
        throw "[$($target.Name)] Compatibility check failed"
    }
}

