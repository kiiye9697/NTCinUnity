[CmdletBinding()]
param([string]$Output = "")
$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$source = Join-Path $repositoryRoot "unity-plugin"
$package = Get-Content -LiteralPath (Join-Path $source "package.json") -Raw | ConvertFrom-Json
if (-not $Output) { $Output = Join-Path $repositoryRoot ("build/NTC-Unity-" + $package.version + ".zip") }
$outputPath = [IO.Path]::GetFullPath($Output)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputPath)) | Out-Null
$packageFiles = @("package.json","package.json.meta","LICENSE.md","LICENSE.md.meta",
    "THIRD-PARTY-NOTICES.md","THIRD-PARTY-NOTICES.md.meta",
    "Runtime.meta","Editor.meta","Resources.meta","Shaders.meta","Tests.meta")
$files = @(Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Name -in $packageFiles })
foreach ($folder in @("Runtime","Editor","Resources","Shaders","Tests")) {
    $files += Get-ChildItem -LiteralPath (Join-Path $source $folder) -Recurse -File
}
$files = @($files | Where-Object {
    $relative = $_.FullName.Substring($source.Length + 1)
    $relative -notmatch '(^|[\\/])README\.md(\.meta)?$' -and
        -not (@($relative.Split([char[]]@('\','/')) | Where-Object { $_.StartsWith('.') }).Count)
} | Sort-Object FullName)
foreach ($file in $files) {
    if ($file.Extension -ne ".meta" -and -not (Test-Path -LiteralPath ($file.FullName + ".meta"))) {
        throw "Missing Unity metadata: $($file.FullName)"
    }
}
Add-Type -AssemblyName System.IO.Compression
$stream = [IO.File]::Open($outputPath, [IO.FileMode]::Create)
try {
    $archive = [IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$true)
    try {
        foreach ($file in $files) {
            $relative = $file.FullName.Substring($source.Length + 1).Replace('\','/')
            $entry = $archive.CreateEntry($relative,[IO.Compression.CompressionLevel]::Optimal)
            $entryStream = $entry.Open()
            try { $inputStream = [IO.File]::OpenRead($file.FullName); try { $inputStream.CopyTo($entryStream) } finally { $inputStream.Dispose() } }
            finally { $entryStream.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }
Write-Output "Package exported: $outputPath ($($files.Count) files; no NtcData, SDK, showcase or hidden IDE files)"
