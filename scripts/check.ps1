$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
Push-Location $root
try {
    git diff --check
    if ($LASTEXITCODE) { throw 'Working tree whitespace check failed.' }
    git log -1 --format=%H | ForEach-Object { git show --check --format= $_ }
    if ($LASTEXITCODE) { throw 'Commit whitespace check failed.' }

    $translations = Get-ChildItem -LiteralPath 'src/WaveTandem.App/Localization' -Filter '*.json'
    $en = Get-Content 'src/WaveTandem.App/Localization/en.json' -Raw | ConvertFrom-Json -AsHashtable
    $sources = Get-ChildItem -LiteralPath 'src/WaveTandem.App' -File -Recurse -Include '*.cs','*.xaml' |
        Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' }
    foreach ($key in ($sources | Select-String -AllMatches '(?:Text\["|Text\[|Text\.Format\(")([A-Za-z]+)(?="|\])' |
            ForEach-Object { $_.Matches.Groups | Where-Object Name -eq '1' | Select-Object -ExpandProperty Value } |
            Sort-Object -Unique)) {
        if (!$en.Contains($key)) { throw "Missing English string $key." }
    }
    foreach ($file in $translations) {
        $locale = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable
        if (Compare-Object ($en.Keys | Sort-Object) ($locale.Keys | Sort-Object)) {
            throw "Mismatched localization keys in $($file.Name)."
        }
        foreach ($key in $en.Keys) {
            if ([string]::IsNullOrWhiteSpace($locale[$key])) { throw "Missing string $key in $($file.Name)." }
            $expected = @([regex]::Matches($en[$key], '\{\d+') | ForEach-Object Value | Sort-Object)
            $actual = @([regex]::Matches($locale[$key], '\{\d+') | ForEach-Object Value | Sort-Object)
            if (($expected -join ',') -ne ($actual -join ',')) { throw "Invalid placeholders for $key in $($file.Name)." }
        }
    }
} finally {
    Pop-Location
}
