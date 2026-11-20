$ErrorActionPreference = "Stop"

try {
    Write-Host "============================================================================" -ForegroundColor Cyan
    Write-Host "Building ChatRenderCLI.exe & ChatRenderGUI.exe (Windows x64 Release)" -ForegroundColor Cyan
    Write-Host "============================================================================" -ForegroundColor Cyan

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw ".NET SDK was not found in PATH. Please install the .NET 10.0 SDK from https://dotnet.microsoft.com/download"
    }

    $ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $CliProjectPath = Join-Path $ScriptDir "TwitchDownloaderCLI\TwitchDownloaderCLI.csproj"
    $WpfProjectPath = Join-Path $ScriptDir "TwitchDownloaderWPF\TwitchDownloaderWPF.csproj"
    $OutputDir = Join-Path $ScriptDir "dist\win-x64"

    $StalePaths = @(
        "TwitchDownloaderCore\ChatRenderer.ExtendedEvents.cs",
        "TwitchDownloaderCore\Chat\ChatHtml.cs",
        "TwitchDownloaderCore\Chat\ChatText.cs",
        "TwitchDownloaderCore\Resources\chat-template.html",
        "TwitchDownloaderCore\Resources\InterVariable.ttf",
        "TwitchDownloaderCore\Resources\InterVariable-Italic.ttf",
        "TwitchDownloaderCore\Extensions\M3U8Extensions.cs",
        "TwitchDownloaderCore\Models\M3U8.cs",
        "TwitchDownloaderCore\Models\M3U8Parse.cs",
        "TwitchDownloaderCore\Models\M3U8VideoQualities.cs",
        "TwitchDownloaderCore\Models\M3U8VideoQuality.cs",
        "TwitchDownloaderCore\Models\ClipVideoQualities.cs",
        "TwitchDownloaderCore\Models\ClipVideoQuality.cs",
        "TwitchDownloaderCore\Models\VideoQualities.cs",
        "TwitchDownloaderCore\Models\Resolution.cs",
        "TwitchDownloaderCore\Models\ThrottledStream.cs",
        "TwitchDownloaderCore\Models\Interfaces",
        "TwitchDownloaderCore\Tools\ClipQualityComparer.cs",
        "TwitchDownloaderCore\Tools\ClipVideoQualityComparer.cs",
        "TwitchDownloaderCore\Tools\CommentIdEqualityComparer.cs",
        "TwitchDownloaderCore\Tools\DownloadTools.cs",
        "TwitchDownloaderCore\Tools\FfmpegConcatList.cs",
        "TwitchDownloaderCore\Tools\FfmpegMetadata.cs",
        "TwitchDownloaderCore\Tools\IdParse.cs",
        "TwitchDownloaderCore\Tools\M3U8StreamQualityComparer.cs",
        "TwitchDownloaderCore\Tools\VideoSizeEstimator.cs",
        "TwitchDownloaderCore\TwitchObjects\Api\UnavailableMedia.cs",
        "TwitchDownloaderCore\TwitchObjects\Gql\GqlClipResponse.cs",
        "TwitchDownloaderCore\TwitchObjects\Gql\GqlClipSearchResponse.cs",
        "TwitchDownloaderCore\TwitchObjects\Gql\GqlClipTokenResponse.cs",
        "TwitchDownloaderCore\TwitchObjects\Gql\GqlCommentResponse.cs",
        "TwitchDownloaderCore\TwitchObjects\Gql\GqlShareClipRenderStatusResponse.cs",
        "TwitchDownloaderCore\TwitchObjects\Gql\GqlUserIdResponse.cs",
        "TwitchDownloaderCore\TwitchObjects\Gql\GqlVideoChapterResponse.cs",
        "TwitchDownloaderCore\TwitchObjects\Gql\GqlVideoResponse.cs",
        "TwitchDownloaderCore\TwitchObjects\Gql\GqlVideoSearchResponse.cs",
        "TwitchDownloaderCore\TwitchObjects\Gql\GqlVideoTokenResponse.cs",
        "TwitchDownloaderCore.Tests\ModelTests\M3U8Tests.cs",
        "TwitchDownloaderCore.Tests\ModelTests\M3U8VideoQualitiesTests.cs",
        "TwitchDownloaderCore.Tests\ToolTests\IdParseTests.cs"
    )
    foreach ($Rel in $StalePaths) {
        $Full = Join-Path $ScriptDir $Rel
        if (Test-Path $Full) {
            Remove-Item -LiteralPath $Full -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Host "`n[1/2] Publishing ChatRenderCLI.exe..." -ForegroundColor Cyan
    dotnet publish $CliProjectPath `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -o $OutputDir

    if ($LASTEXITCODE -ne 0) {
        throw "CLI build failed with exit code $LASTEXITCODE."
    }

    Write-Host "`n[2/2] Publishing ChatRenderGUI.exe (WPF)..." -ForegroundColor Cyan
    dotnet publish $WpfProjectPath `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:IncludeAllContentForSelfExtract=true `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -o $OutputDir

    if ($LASTEXITCODE -ne 0) {
        throw "GUI build failed with exit code $LASTEXITCODE."
    }

    Write-Host ""
    Write-Host "[SUCCESS] Built standalone executables:" -ForegroundColor Green
    Write-Host "  $(Join-Path $OutputDir 'ChatRenderCLI.exe')" -ForegroundColor Green
    Write-Host "  $(Join-Path $OutputDir 'ChatRenderGUI.exe')" -ForegroundColor Green
    Write-Host ""
    Read-Host "Press Enter to close this window"
}
catch {
    Write-Host ""
    Write-Host "[ERROR] $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Read-Host "Build failed. Press Enter to close this window"
    exit 1
}
