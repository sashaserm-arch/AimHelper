$ErrorActionPreference = 'Stop'
$version = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid VERSION file.' }
$exe = Join-Path $PSScriptRoot 'bin\Release\AngleReader.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Run package.cmd to build and test first.' }
$dist = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$archive = Join-Path $dist "AngleReader-v$version-win-x64.zip"
Add-Type -AssemblyName System.IO.Compression
$stream = [System.IO.File]::Open($archive, [System.IO.FileMode]::Create)
try {
    $zip = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        $files = [ordered]@{
            'AngleReader.exe' = $exe
            'AngleReader.exe.config' = (Join-Path $PSScriptRoot 'bin\Release\AngleReader.exe.config')
            'USER_GUIDE.txt' = (Join-Path $PSScriptRoot 'docs\USER_GUIDE.txt')
        }
        foreach ($name in $files.Keys) {
            $entry = $zip.CreateEntry($name)
            $input = [System.IO.File]::OpenRead($files[$name])
            $output = $entry.Open()
            try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
        }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }
$algorithm = [System.Security.Cryptography.SHA256]::Create()
$input = [System.IO.File]::OpenRead($archive)
try {
    $hash = [System.BitConverter]::ToString($algorithm.ComputeHash($input)).Replace('-', '').ToLowerInvariant()
} finally { $input.Dispose(); $algorithm.Dispose() }
[System.IO.File]::WriteAllText("$archive.sha256", "$hash  $([System.IO.Path]::GetFileName($archive))`n", [System.Text.Encoding]::ASCII)
Write-Output "Package: $archive"
Write-Output "SHA256: $hash"
