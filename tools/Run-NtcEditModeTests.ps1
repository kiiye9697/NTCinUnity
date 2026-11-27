[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$UnityEditor,
    [string]$ProjectPath = "",
    [string]$Corpus = "",
    [string]$UrpVersion = "",
    [string]$RegistryUrl = "",
    [switch]$CpuOnly
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$editorPath = [IO.Path]::GetFullPath($UnityEditor)
if (-not (Test-Path -LiteralPath $editorPath -PathType Leaf)) { throw "Unity editor not found: $editorPath" }
if (-not $ProjectPath) { $ProjectPath = Join-Path $repositoryRoot "build\ntc-review-tests" }
$testProject = [IO.Path]::GetFullPath($ProjectPath)
if (-not $Corpus -and -not $CpuOnly) { $Corpus = Join-Path $repositoryRoot "unity-plugin\NtcData" }
if ($Corpus) {
    if (-not (Test-Path -LiteralPath $Corpus -PathType Container)) { throw "NTC corpus not found: $Corpus. Full GPU validation requires the 25-file showcase corpus; use -CpuOnly without -Corpus for the synthetic subset." }
    if (@(Get-ChildItem -LiteralPath $Corpus -Filter *.ntc -File).Count -ne 25) { throw "Expected the 25-file showcase corpus. Omit -Corpus with -CpuOnly to run synthetic tests without it." }
}

# This manifest belongs to a disposable test host, never the user's Showcase.
New-Item -ItemType Directory -Force -Path (Join-Path $testProject "Assets"),(Join-Path $testProject "Packages") | Out-Null
$testPackage = Join-Path $testProject "LocalPackages/com.kiiye.ntc-unity"
[IO.Directory]::CreateDirectory($testPackage) | Out-Null
foreach ($folder in @("Runtime","Editor","Shaders","Resources","Tests")) {
    $destination = Join-Path $testPackage $folder
    [IO.Directory]::CreateDirectory($destination) | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "unity-plugin/$folder") -Force | Copy-Item -Destination $destination -Recurse -Force
}
Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "unity-plugin") -File | Copy-Item -Destination $testPackage -Force
$dependencies = @{
    "com.kiiye.ntc-unity" = "file:../LocalPackages/com.kiiye.ntc-unity"
    "com.unity.test-framework" = "1.1.31"
    "com.unity.modules.imageconversion" = "1.0.0"
    "com.unity.modules.imgui" = "1.0.0"
    "com.unity.modules.physics" = "1.0.0"
    "com.unity.modules.jsonserialize" = "1.0.0"
}
$builtinRoot = Join-Path (Split-Path $editorPath) "Data\Resources\PackageManager\BuiltInPackages"
if (Test-Path -LiteralPath (Join-Path $builtinRoot "com.unity.render-pipelines.universal\package.json")) {
    foreach ($package in @("com.unity.render-pipelines.universal","com.unity.render-pipelines.core","com.unity.shadergraph")) {
        $dependencies[$package] = "file:" + (Join-Path $builtinRoot $package).Replace('\','/')
    }
} elseif ($UrpVersion) {
    $dependencies["com.unity.render-pipelines.universal"] = $UrpVersion
} else {
    throw "This editor does not bundle URP. Supply -UrpVersion with a version compatible with it."
}
$manifest = @{ dependencies = $dependencies; testables = @("com.kiiye.ntc-unity") }
if ($RegistryUrl) { $manifest.scopedRegistries = @(@{ name = "Unity packages"; url = $RegistryUrl; scopes = @("com.unity") }) }
$manifest | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $testProject "Packages\manifest.json") -Encoding UTF8

$results = Join-Path $testProject "editmode-results.xml"
$log = Join-Path $testProject "editmode.log"
$arguments = @("-batchmode", "-projectPath", ('"' + $testProject + '"'), "-runTests", "-testPlatform", "EditMode",
    "-testResults", ('"' + $results + '"'), "-logFile", ('"' + $log + '"'))
if ($CpuOnly) { $arguments += "-nographics" } else { $arguments += "-force-d3d12" }
$previousCorpus = $env:NTC_TEST_DATA_DIR
try {
    $env:NTC_TEST_DATA_DIR = if ($Corpus) { [IO.Path]::GetFullPath($Corpus) } else { $null }
    # Remove only a previous result file so a startup failure cannot report stale success.
    if (Test-Path -LiteralPath $results) { Remove-Item -LiteralPath $results }
    $run = Start-Process -FilePath $editorPath -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    if ($run.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $results)) { throw "Unity tests did not complete. See $log" }
    [xml]$document = Get-Content -LiteralPath $results -Raw
    $summary = $document.'test-run'
    if ($null -eq $summary -or [int]$summary.passed -lt 1) { throw "No passing NTC tests were reported. See $results" }
    if ([int]$summary.failed -gt 0) { throw "NTC tests failed: $($summary.failed). See $results" }
    if (-not $CpuOnly -and [int]$summary.skipped -gt 0) { throw "Full validation requires no skipped tests. See $results" }
    $validation = if ($CpuOnly) { "CPU subset (not full validation)" } else { "Full GPU validation" }
    Write-Output "NTC $validation`: $($summary.passed) passed, $($summary.failed) failed, $($summary.skipped) skipped. Results: $results"
} finally {
    $env:NTC_TEST_DATA_DIR = $previousCorpus
}
