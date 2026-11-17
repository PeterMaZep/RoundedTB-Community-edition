# Stamps a release tag (vX.Y.Z) into every file that carries the version, so
# cutting a release no longer needs a "Bump version strings" commit:
#   PackagingProject\Package.appxmanifest  Identity Version="X.Y.Z.0"
#   RoundedTB\AboutWindow.xaml(.cs)         "Community Edition vX.Y.Z"
# Tags up to v0.4.2 shipped as MSIX 2.0.Y.Z; from v2.1.0 the tag and the MSIX
# version are the same number. Tags below v2.1.0 are rejected: v0.x would
# install as a downgrade of 2.0.4.2, and v2.0.x would collide with the old scheme.
param(
    [Parameter(Mandatory)] [string] $Tag,
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'

if ($Tag -notmatch '^v(\d+)\.(\d+)\.(\d+)$') {
    throw "Tag '$Tag' is not of the form vX.Y.Z"
}
$semver = "$($Matches[1]).$($Matches[2]).$($Matches[3])"
if ([version]$semver -lt [version]'2.1.0') {
    throw "Tag '$Tag' is below v2.1.0; releases after v0.4.2 (MSIX 2.0.4.2) start at v2.1.0"
}
$msixVersion = "$semver.0"

function Update-File([string] $RelativePath, [string] $Pattern, [string] $Replacement) {
    $path = Join-Path $Root $RelativePath
    $bytes = [IO.File]::ReadAllBytes($path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = [IO.File]::ReadAllText($path)
    if ($text -notmatch $Pattern) {
        throw "Version pattern not found in $RelativePath"
    }
    $updated = [regex]::Replace($text, $Pattern, $Replacement)
    # Keep each file's BOM state so the stamp is the only diff.
    [IO.File]::WriteAllText($path, $updated, (New-Object Text.UTF8Encoding($hasBom)))
    Write-Host "$RelativePath -> $(([regex]::Match($updated, $Pattern)).Value)"
}

Update-File 'PackagingProject\Package.appxmanifest' '(?<=<Identity[^>]*\sVersion=")[\d.]+(?=")' $msixVersion
Update-File 'RoundedTB\AboutWindow.xaml' '(?<=Community Edition v)[\d.]+' $semver
Update-File 'RoundedTB\AboutWindow.xaml.cs' '(?<=Community Edition v)[\d.]+' $semver
