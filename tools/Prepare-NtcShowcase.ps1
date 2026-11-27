[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$UnityEditor,
    [string]$Project = "",
    [string]$Scene = "Assets/ClassicSponza/Scenes/Sponza_Showcase.unity",
    [string]$PipelineAsset = "",
    [string]$LegacyDirectory = "",
    [string]$RegistryUrl = "",
    [ValidateSet("Original", "InferenceOnLoad", "InferenceOnSample", "InferenceOnSampleDirect")][string]$InitialMode = "Original",
    [switch]$ReleaseOriginalTextures,
    [switch]$InitializePluginMirror,
    [switch]$UseBundledPackages,
    [switch]$SyncOnly
)
$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($Project)) { $Project = Join-Path $repositoryRoot "unity-showcase" }
$projectPath = [IO.Path]::GetFullPath($Project)
if (-not (Test-Path -LiteralPath $UnityEditor -PathType Leaf)) { throw "Unity editor not found: $UnityEditor" }
if (-not (Test-Path -LiteralPath (Join-Path $projectPath "ProjectSettings") -PathType Container)) { throw "Not a Unity project: $projectPath" }
$lockPath = Join-Path $projectPath "Temp/UnityLockfile"
if (Test-Path -LiteralPath $lockPath) {
    try { $lockProbe = [IO.File]::Open($lockPath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); $lockProbe.Dispose() }
    catch { throw "Close this Unity project before synchronizing source and packages." }
}
$sourceRoot = Join-Path $repositoryRoot "unity-plugin"
$targetRoot = Join-Path $projectPath "Assets/NtcPlugin"
$statePath = Join-Path $projectPath "ProjectSettings/NtcPluginMirror.json"
$previous = @{}
if (Test-Path -LiteralPath $statePath) {
    $stored = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    foreach ($property in $stored.PSObject.Properties) { $previous[$property.Name] = [string]$property.Value }
}
$files = @()
foreach ($folder in @("Runtime", "Editor", "Shaders", "Resources", "Tests")) {
    $files += Get-ChildItem -LiteralPath (Join-Path $sourceRoot $folder) -Recurse -File
}
$files += Get-ChildItem -LiteralPath $sourceRoot -File
$plan = @()
foreach ($file in $files) {
    $relative = $file.FullName.Substring($sourceRoot.Length + 1)
    $target = [IO.Path]::GetFullPath((Join-Path $targetRoot $relative))
    $sourceHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $targetHash = if (Test-Path -LiteralPath $target -PathType Leaf) { (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash } else { "" }
    if ($targetHash -and $targetHash -ne $sourceHash -and (-not $previous.ContainsKey($relative) -or $previous[$relative] -ne $targetHash) -and -not $InitializePluginMirror) {
        throw "Untracked/local showcase plugin changes: $relative. Inspect them first; -InitializePluginMirror explicitly backs them up before initializing/replacing the mirror. Canonical source is never modified."
    }
    $plan += [pscustomobject]@{ Source = $file.FullName; Relative = $relative; Target = $target; Hash = $sourceHash; Changed = ($targetHash -ne $sourceHash); Existing = [bool]$targetHash }
}
$backupRoot = Join-Path $repositoryRoot ("build/prepare-backups/" + (Get-Date -Format "yyyyMMdd-HHmmss-ffff"))
$next = @{}
foreach ($item in $plan) {
    if ($item.Changed) {
        if ($item.Existing) {
            $backup = Join-Path $backupRoot $item.Relative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup)) | Out-Null
            Copy-Item -LiteralPath $item.Target -Destination $backup
        }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($item.Target)) | Out-Null
        Copy-Item -LiteralPath $item.Source -Destination $item.Target -Force
    }
    $next[$item.Relative] = $item.Hash
}
$next | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $statePath -Encoding UTF8
Write-Host "Plugin synchronized (Runtime/Editor/Shaders/Resources/Tests and metadata): $targetRoot"
if (Test-Path -LiteralPath $backupRoot) { Write-Host "Preserved displaced showcase files: $backupRoot" }
if ($UseBundledPackages) {
    $bundleRoot = Join-Path (Split-Path -Parent $UnityEditor) "Data/Resources/PackageManager/BuiltInPackages"
    $manifestPath = Join-Path $projectPath "Packages/manifest.json"
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $urpManifest = Get-Content -LiteralPath (Join-Path $bundleRoot "com.unity.render-pipelines.universal/package.json") -Raw | ConvertFrom-Json
    foreach ($foundation in @("com.unity.burst", "com.unity.mathematics")) {
        $manifest.dependencies | Add-Member -NotePropertyName $foundation -NotePropertyValue $urpManifest.dependencies.$foundation -Force
    }
    # Burst/mathematics remain portable registry dependencies; this Editor bundles
    # the render-pipeline packages but does not bundle Burst or mathematics.
    foreach ($package in @("com.unity.render-pipelines.core", "com.unity.render-pipelines.universal", "com.unity.shadergraph")) {
        $packageSource = Join-Path $bundleRoot $package
        $packageTarget = Join-Path $projectPath "LocalPackages/$package"
        if (-not (Test-Path -LiteralPath (Join-Path $packageSource "package.json"))) { throw "Editor bundle does not contain $package; use registry resolution instead." }
        if (Test-Path -LiteralPath $packageTarget) {
            $packageFiles = @(Get-ChildItem -LiteralPath $packageSource -Recurse -File -Force)
            $targetFiles = @(Get-ChildItem -LiteralPath $packageTarget -Recurse -File -Force)
            if ($packageFiles.Count -ne $targetFiles.Count) { throw "Local package differs from the Editor bundle: $packageTarget. Existing local source is preserved." }
            foreach ($file in $packageFiles) {
                $relative = $file.FullName.Substring($packageSource.Length + 1)
                $existing = Join-Path $packageTarget $relative
                if (-not (Test-Path -LiteralPath $existing) -or (Get-FileHash -LiteralPath $existing).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
                    throw "Local package has edits: $existing. Existing local source is preserved."
                }
            }
        }
        else {
            [IO.Directory]::CreateDirectory($packageTarget) | Out-Null
            Get-ChildItem -LiteralPath $packageSource -Force | Copy-Item -Destination $packageTarget -Recurse
        }
        $manifest.dependencies | Add-Member -NotePropertyName $package -NotePropertyValue "file:../LocalPackages/$package" -Force
    }
    $manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    Write-Host "Prepared portable project-relative local packages. Unity will regenerate the dependency lock."
}
if ($RegistryUrl) {
    $manifestPath = Join-Path $projectPath "Packages/manifest.json"
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $registries = @($manifest.scopedRegistries | Where-Object { $_ -and $_.name -ne "NTC Unity packages" })
    $registries += @{ name="NTC Unity packages"; url=$RegistryUrl; scopes=@("com.unity") }
    $manifest | Add-Member -NotePropertyName scopedRegistries -NotePropertyValue $registries -Force
    $manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
}
if ($SyncOnly) { return }
$logDirectory = Join-Path $repositoryRoot "build/prepare-results"
[IO.Directory]::CreateDirectory($logDirectory) | Out-Null
$log = Join-Path $logDirectory "unity-prepare.log"
$arguments = @("-batchmode", "-quit", "-projectPath", ('"' + $projectPath + '"'), "-executeMethod", "Ntc.UnityEditorTools.NtcShowcasePreparation.Prepare", "-ntcBenchmarkScene", ('"' + $Scene + '"'), "-ntcInitialMode", $InitialMode, "-logFile", ('"' + $log + '"'))
if ($PipelineAsset) { $arguments += @("-ntcPipelineAsset", ('"' + $PipelineAsset + '"')) }
if ($LegacyDirectory) { $arguments += @("-ntcLegacyDirectory", ('"' + [IO.Path]::GetFullPath($LegacyDirectory) + '"')) }
if ($ReleaseOriginalTextures) { $arguments += "-ntcReleaseOriginalTextures" }
$process = Start-Process -FilePath $UnityEditor -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0) { throw "Showcase preparation failed ($($process.ExitCode)); see $log" }
Write-Host "Showcase preparation completed: $log"
