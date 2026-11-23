# Checks every Strings.<lang>.resx against Strings.resx: same keys, same {n} placeholders, well-formed XML.
# Run:  powershell -NoProfile -File Tools\Check-Translations.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$dir = Join-Path $root 'HoboLibris\Resources'
[xml]$en = Get-Content -LiteralPath (Join-Path $dir 'Strings.resx') -Encoding UTF8
$enMap = @{}
foreach ($d in $en.root.data) { $enMap[$d.name] = [string]$d.value }
$problems = 0
foreach ($file in Get-ChildItem -LiteralPath $dir -Filter 'Strings.*.resx') {
    if ($file.Name -eq 'Strings.resx') { continue }
    try {
        [xml]$doc = Get-Content -LiteralPath $file.FullName -Encoding UTF8
    } catch {
        Write-Host "$($file.Name): not well-formed XML: $($_.Exception.Message)"
        $problems++
        continue
    }
    $map = @{}
    foreach ($d in $doc.root.data) { $map[$d.name] = [string]$d.value }
    $missing = @($enMap.Keys | Where-Object { -not $map.ContainsKey($_) })
    $extra = @($map.Keys | Where-Object { -not $enMap.ContainsKey($_) })
    $placeholders = @()
    $untranslated = 0
    foreach ($k in $enMap.Keys) {
        if (-not $map.ContainsKey($k)) { continue }
        $a = [regex]::Matches($enMap[$k], '\{\d\}') | ForEach-Object { $_.Value } | Sort-Object
        $b = [regex]::Matches($map[$k], '\{\d\}') | ForEach-Object { $_.Value } | Sort-Object
        if (($a -join ',') -ne ($b -join ',')) { $placeholders += $k }
        if ($map[$k] -eq $enMap[$k] -and $enMap[$k].Length -gt 12 -and $enMap[$k] -notmatch '^[A-Za-z]+://') { $untranslated++ }
        if ([string]::IsNullOrWhiteSpace($map[$k])) { $placeholders += "$k (empty)" }
    }
    $status = if ($missing.Count -eq 0 -and $extra.Count -eq 0 -and $placeholders.Count -eq 0) { 'ok' } else { 'PROBLEMS' }
    Write-Host ("{0}: {1} keys, {2} ({3} left in English)" -f $file.Name, $map.Count, $status, $untranslated)
    if ($missing.Count) { Write-Host "  missing: $($missing -join ', ')"; $problems++ }
    if ($extra.Count) { Write-Host "  extra: $($extra -join ', ')"; $problems++ }
    if ($placeholders.Count) { Write-Host "  placeholders differ: $($placeholders -join ', ')"; $problems++ }
}
if ($problems -eq 0) { Write-Host 'All translations match the English keys.' } else { exit 1 }
