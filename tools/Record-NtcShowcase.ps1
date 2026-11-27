[CmdletBinding()]
param(
    [string]$UnityEditor = "",
    [string]$Project = "",
    [string]$Player = "",
    [string]$Output = "",
    [string]$Encoder = "",
    [int]$Width = 1600,
    [int]$Height = 900,
    [int]$Frames = 0,
    [float]$CameraHeight = [float]::NaN,
    [switch]$Build,
    [switch]$ReviewPage,
    [switch]$LightingReview
)
$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if (-not $Project) { $Project = Join-Path $repositoryRoot "unity-showcase" }
if (-not $Player) { $Player = Join-Path $repositoryRoot "build/NTCinUnity-Presentation/NTCinUnity.exe" }
if (-not $Output) { $Output = Join-Path $repositoryRoot "build/showcase-recording" }
$playerPath = [IO.Path]::GetFullPath($Player)
$outputPath = [IO.Path]::GetFullPath($Output)
if (-not $LightingReview -and -not $Encoder) {
    $command = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($command) { $Encoder = $command.Source }
    else {
        $python = Get-Command python -ErrorAction SilentlyContinue
        if ($python) { $Encoder = (& $python.Source -c "import imageio_ffmpeg; print(imageio_ffmpeg.get_ffmpeg_exe())" 2>$null) }
    }
}
if (-not $LightingReview -and (-not $Encoder -or -not (Test-Path -LiteralPath $Encoder -PathType Leaf))) { throw "Supply an installed ffmpeg executable with -Encoder." }
[IO.Directory]::CreateDirectory($outputPath) | Out-Null
if ($Build) {
    if (-not (Test-Path -LiteralPath $UnityEditor -PathType Leaf)) { throw "-Build requires -UnityEditor." }
    & (Join-Path $PSScriptRoot "Prepare-NtcShowcase.ps1") -UnityEditor $UnityEditor -Project $Project -SyncOnly
    $arguments = @("-batchmode","-quit","-projectPath",('"'+[IO.Path]::GetFullPath($Project)+'"'),
        "-executeMethod","Ntc.UnityEditorTools.NtcShowcaseBuild.BuildWindows64",
        "-ntcShowcasePlayer",('"'+$playerPath+'"'),"-logFile",('"'+(Join-Path $outputPath "build.log")+'"'))
    if (-not [float]::IsNaN($CameraHeight)) {
        $arguments += @("-ntcCameraHeight",$CameraHeight.ToString([Globalization.CultureInfo]::InvariantCulture))
    }
    $buildProcess = Start-Process -FilePath $UnityEditor -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    if ($buildProcess.ExitCode -ne 0) { throw "Showcase build failed. See build.log." }
}
if (-not (Test-Path -LiteralPath $playerPath -PathType Leaf)) { throw "Presentation Player not found: $playerPath. Build it with -Build." }
$video = Join-Path $outputPath "NTCinUnity-Showcase-AB.mp4"
$arguments = @("-screen-fullscreen","0","-screen-width",$Width,"-screen-height",$Height,"-force-d3d12",
    "-ntcShowcaseOutput",('"'+$outputPath+'"'),"-ntcShowcaseAutoQuit",
    "-logFile",('"'+(Join-Path $outputPath "player.log")+'"'))
if ($LightingReview) { $arguments += "-ntcReviewLighting" } else { $arguments += @("-ntcShowcaseRecord","-ntcShowcaseEncoder",('"'+[IO.Path]::GetFullPath($Encoder)+'"')) }
if ($Frames -gt 0) { $arguments += @("-ntcShowcaseFrames",$Frames) }
# Keep the player visible while it captures both camera passes.
$run = Start-Process -FilePath $playerPath -ArgumentList $arguments -WindowStyle Normal -Wait -PassThru
if ($run.ExitCode -ne 0) { throw "Showcase Player failed. See player.log." }
if ($LightingReview) {
    foreach ($name in @("source","skylight","daylight")) {
        if (-not (Test-Path -LiteralPath (Join-Path $outputPath ("lighting-"+$name+".png")))) { throw "Missing lighting image: $name" }
    }
    Write-Output "Lighting views saved: $outputPath"
} else {
    $original = Join-Path $outputPath "Original.mp4"
    $ntc = Join-Path $outputPath "NTC-Direct.mp4"
    if (-not (Test-Path -LiteralPath $original) -or -not (Test-Path -LiteralPath $ntc)) { throw "Both camera passes must exist." }
    $filter = "[1:v]crop=iw/2:ih:iw/2:0[right];[0:v][right]overlay=x=main_w/2:y=0,drawbox=x=iw/2-1:y=0:w=2:h=ih:color=white:t=fill,drawbox=x=16:y=16:w=125:h=30:color=black@0.5:t=fill,drawbox=x=iw-141:y=16:w=125:h=30:color=black@0.5:t=fill,drawtext=fontfile='C\:/Windows/Fonts/arial.ttf':text='A Original':x=28:y=22:fontsize=18:fontcolor=white,drawtext=fontfile='C\:/Windows/Fonts/arial.ttf':text='B NTC':x=w-128:y=22:fontsize=18:fontcolor=white[v]"
    & $Encoder -hide_banner -y -loglevel warning -i $original -i $ntc -filter_complex $filter -map '[v]' -an -c:v libx264 -preset fast -crf 18 -pix_fmt yuv420p -movflags +faststart $video
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $video)) { throw "A/B composition failed." }
    Write-Output "Showcase recorded: $video"
    if ($ReviewPage) {
        & python (Join-Path $PSScriptRoot "Build-NtcReview.py") --project $Project --recording $outputPath --output (Join-Path $outputPath "review")
        if ($LASTEXITCODE -ne 0) { throw "Review page generation failed." }
    }
}
