[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$UnityEditor,
    [string]$Project = "",
    [string]$Player = "",
    [string]$Output = "",
    [string]$Scene = "Assets/ClassicSponza/Scenes/Sponza_Showcase.unity",
    [switch]$InitializePluginMirror,
    [switch]$SkipPrepare,
    [switch]$ResourceOnly,
    [string]$Modes = "Original,InferenceOnLoad,InferenceOnSample,InferenceOnSampleDirect",
    [int]$WarmupFrames = 180,
    [int]$SampleFrames = 600,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($Project)) { $Project = Join-Path $repositoryRoot "unity-showcase" }
if ([string]::IsNullOrWhiteSpace($Player)) { $Player = Join-Path $repositoryRoot "build\NTCinUnity-Benchmark\NTCinUnity.exe" }
if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path $repositoryRoot "build\benchmark-results" }
$projectPath = [IO.Path]::GetFullPath($Project)
$playerPath = [IO.Path]::GetFullPath($Player)
$outputPath = [IO.Path]::GetFullPath($Output)
$pluginSource = Join-Path $repositoryRoot "unity-plugin"
$pluginTarget = Join-Path $projectPath "Assets\NtcPlugin"

if (-not (Test-Path -LiteralPath $projectPath -PathType Container)) { throw "Unity project not found: $projectPath" }
if (-not (Test-Path -LiteralPath $pluginSource -PathType Container)) { throw "Plugin source not found: $pluginSource" }

if (-not $SkipBuild -and -not $SkipPrepare) {
    & (Join-Path $PSScriptRoot "Prepare-NtcShowcase.ps1") -UnityEditor $UnityEditor -Project $projectPath -Scene $Scene -InitializePluginMirror:$InitializePluginMirror
}

if (-not $SkipBuild) {
    if (-not (Test-Path -LiteralPath $UnityEditor -PathType Leaf)) { throw "Unity editor not found: $UnityEditor" }
    $buildLog = Join-Path $outputPath "unity-build.log"
    New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
    $buildArgs = @(
        "-batchmode", "-quit", "-projectPath", ('"' + $projectPath + '"'),
        "-executeMethod", "Ntc.Unity.EditorTools.NtcBenchmarkBuild.BuildWindows64",
        "-ntcBenchmarkPlayer", ('"' + $playerPath + '"'), "-logFile", ('"' + $buildLog + '"')
        "-ntcBenchmarkScene", ('"' + $Scene + '"')
    )
    $build = Start-Process -FilePath $UnityEditor -ArgumentList $buildArgs -Wait -PassThru -WindowStyle Hidden
    if ($build.ExitCode -ne 0) { throw "Unity build failed with exit code $($build.ExitCode). See $buildLog" }
}

if (-not (Test-Path -LiteralPath $playerPath -PathType Leaf)) { throw "Benchmark Player not found: $playerPath" }
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
$playerLog = Join-Path $outputPath "player.log"
$reportPath = Join-Path $outputPath "benchmark.json"
if (Test-Path -LiteralPath $reportPath) { Remove-Item -LiteralPath $reportPath }
$runArgs = @(
    # Do not use -batchmode: it skips normal rendering, makes Renderer.isVisible false,
    # and invalidates on-Sample residency/performance results.
    "-screen-fullscreen", "0", "-screen-width", "1920", "-screen-height", "1080",
    "-force-d3d12", "-ntcBenchmark", "-ntcBenchmarkModes", $Modes, "-ntcBenchmarkOutput", ('"' + $outputPath + '"'),
    "-ntcBenchmarkWarmupFrames", $WarmupFrames,
    "-ntcBenchmarkSampleFrames", $SampleFrames,
    "-logFile", ('"' + $playerLog + '"')
)
if ($ResourceOnly) { $runArgs += "-ntcBenchmarkAllowNoRenderedFrame" }
$run = Start-Process -FilePath $playerPath -ArgumentList $runArgs -Wait -PassThru -WindowStyle Hidden
if ($run.ExitCode -ne 0) { throw "Benchmark Player failed with exit code $($run.ExitCode). See $playerLog" }
if (-not (Test-Path -LiteralPath $reportPath)) { throw "Player did not write a fresh benchmark.json. See $playerLog" }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
$expectedModes = @($Modes.Split(',') | ForEach-Object { $_.Trim() })
foreach ($mode in $expectedModes) {
    $result = @($report.modes | Where-Object mode -eq $mode)
    if ($result.Count -ne 1 -or -not $result[0].completed -or -not $result[0].gpuFencePassed -or $result[0].afterSamples.failedGroups -gt 0) {
        throw "Mode $mode failed acceptance. See $reportPath"
    }
}
if (-not $ResourceOnly -and -not $report.framePerformanceValid) { throw "No rendered camera frames: frame performance is invalid. Use -ResourceOnly explicitly for resource/queue validation." }
if ($ResourceOnly) { Write-Host "Resource/queue validation complete (not frame performance): $outputPath" }
else { Write-Host "Rendered benchmark complete: $outputPath" }
