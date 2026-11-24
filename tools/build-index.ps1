<#
.SYNOPSIS
    Validates the themes and (re)generates index.json, the catalogue read by NeoIDE.

.PARAMETER Check
    CI mode: fails when a theme is invalid or when index.json is not up to date.

.EXAMPLE
    pwsh tools/build-index.ps1          # regenerate index.json after adding / changing a theme
    pwsh tools/build-index.ps1 -Check   # what the GitHub Action runs
#>
param([switch]$Check)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$errors = @()
$themes = @()
$ids = @{}

function Get-Color($value) {
    if ($null -eq $value) { return $null }
    if ($value -is [string]) { return $value }
    return $value.color
}

foreach ($file in Get-ChildItem (Join-Path $root "themes") -Filter *.json | Sort-Object Name) {
    try { $t = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8) | ConvertFrom-Json }
    catch { $errors += "$($file.Name): invalid JSON ($($_.Exception.Message))"; continue }

    foreach ($field in "id", "name", "version", "author", "description") {
        if (-not $t.$field) { $errors += "$($file.Name): missing '$field'" }
    }
    if ($null -eq $t.dark) { $errors += "$($file.Name): missing 'dark' (true / false)" }
    if ($t.id -and $file.BaseName -ne $t.id) { $errors += "$($file.Name): the file must be named '$($t.id).json'" }
    if ($t.id -and $t.id -notmatch '^[a-z0-9][a-z0-9-]*$') { $errors += "$($file.Name): id must be lowercase letters, digits and dashes" }
    if ($t.version -and $t.version -notmatch '^\d+\.\d+\.\d+$') { $errors += "$($file.Name): version must look like 1.2.3" }
    if ($t.id -and $ids.ContainsKey($t.id)) { $errors += "$($file.Name): duplicate id '$($t.id)'" }
    if ($t.id) { $ids[$t.id] = $true }

    $hex = '^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$'
    foreach ($section in "ui", "editor") {
        if ($t.$section) { foreach ($p in $t.$section.PSObject.Properties) { if ($p.Value -notmatch $hex) { $errors += "$($file.Name): $section.$($p.Name) is not a #RRGGBB colour" } } }
    }
    if ($t.syntax) { foreach ($p in $t.syntax.PSObject.Properties) { $c = Get-Color $p.Value; if ($c -notmatch $hex) { $errors += "$($file.Name): syntax.$($p.Name) is not a #RRGGBB colour" } } }

    $dark = [bool]$t.dark
    $themes += [ordered]@{
        id          = $t.id
        name        = $t.name
        version     = $t.version
        author      = $t.author
        description = $t.description
        dark        = $dark
        file        = "themes/$($file.Name)"
        preview     = [ordered]@{
            background = if ($t.editor.background) { $t.editor.background } elseif ($dark) { "#1E1F22" } else { "#FFFFFF" }
            foreground = if (Get-Color $t.syntax.Default) { Get-Color $t.syntax.Default } elseif ($dark) { "#BCBEC4" } else { "#080808" }
            keyword    = Get-Color $t.syntax.Keyword
            string     = Get-Color $t.syntax.String
            function   = Get-Color $t.syntax.Function
            number     = Get-Color $t.syntax.Number
            variable   = Get-Color $t.syntax.Variable
            panel      = if ($t.ui.PanelBg) { $t.ui.PanelBg } elseif ($dark) { "#2B2D30" } else { "#F7F8FA" }
            accent     = if ($t.ui.Accent) { $t.ui.Accent } else { "#3574F0" }
        }
    }
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Host "ERROR $_" -ForegroundColor Red }
    exit 1
}

# Own serializer: same output with Windows PowerShell 5.1 and pwsh 7 (ConvertTo-Json formats differently).
function Format-Json($value, [int]$level = 0) {
    $pad = "  " * ($level + 1); $end = "  " * $level
    if ($null -eq $value) { return "null" }
    if ($value -is [bool]) { return $(if ($value) { "true" } else { "false" }) }
    if ($value -is [int] -or $value -is [long] -or $value -is [double]) { return "$value" }
    if ($value -is [string]) {
        $s = $value.Replace('\', '\\').Replace('"', '\"').Replace("`n", '\n').Replace("`r", '\r').Replace("`t", '\t')
        return '"' + $s + '"'
    }
    if ($value -is [System.Collections.IDictionary]) {
        if ($value.Count -eq 0) { return "{}" }
        $items = foreach ($k in $value.Keys) { $pad + '"' + $k + '": ' + (Format-Json $value[$k] ($level + 1)) }
        return "{`n" + ($items -join ",`n") + "`n$end}"
    }
    $list = @($value)
    if ($list.Count -eq 0) { return "[]" }
    $items = foreach ($v in $list) { $pad + (Format-Json $v ($level + 1)) }
    return "[`n" + ($items -join ",`n") + "`n$end]"
}

$index = [ordered]@{ version = 1; themes = $themes; plugins = @() }
$json = Format-Json $index
$path = Join-Path $root "index.json"
if ($Check) {
    $current = if (Test-Path $path) { ([System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)) -replace "`r`n", "`n" } else { "" }
    if ($current.Trim() -ne $json.Trim()) {
        Write-Host "ERROR index.json is not up to date: run 'pwsh tools/build-index.ps1' and commit the result." -ForegroundColor Red
        exit 1
    }
    Write-Host "OK: $($themes.Count) theme(s), index.json up to date." -ForegroundColor Green
}
else {
    [System.IO.File]::WriteAllText($path, $json + "`n", (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "index.json written: $($themes.Count) theme(s)." -ForegroundColor Green
}
