param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Label,
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "Version '$Version' is not of the form X.Y.Z.W"
}

function Update-File([string] $RelativePath, [string] $Pattern, [string] $Replacement) {
    $path = Join-Path $Root $RelativePath
    $bytes = [IO.File]::ReadAllBytes($path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = [IO.File]::ReadAllText($path)
    if ($text -notmatch $Pattern) {
        throw "Version pattern not found in $RelativePath"
    }
    $updated = [regex]::Replace($text, $Pattern, $Replacement)
    [IO.File]::WriteAllText($path, $updated, (New-Object Text.UTF8Encoding($hasBom)))
    Write-Host "$RelativePath -> $(([regex]::Match($updated, $Pattern)).Value)"
}

Update-File 'PackagingProject\Package.appxmanifest' '(?<=<Identity[^>]*\sVersion=")[\d.]+(?=")' $Version
Update-File 'RoundedTB\AboutWindow.xaml' '(?<=Community Edition v)[^"<\r\n]+' $Label
Update-File 'RoundedTB\AboutWindow.xaml.cs' '(?<=Community Edition v)[^"<\r\n]+' $Label
