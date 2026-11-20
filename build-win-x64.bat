@echo off
setlocal

echo ============================================================================
echo Building ChatRenderCLI.exe ^& ChatRenderGUI.exe (Windows x64 Release)
echo ============================================================================

where dotnet >nul 2>nul
if %ERRORLEVEL% neq 0 (
    echo [ERROR] .NET SDK was not found in PATH.
    echo Please install the .NET 10.0 SDK from https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

echo Cleaning any stale files from previous versions...
for %%F in (
    "TwitchDownloaderCore\ChatRenderer.ExtendedEvents.cs"
    "TwitchDownloaderCore\Chat\ChatHtml.cs"
    "TwitchDownloaderCore\Chat\ChatText.cs"
    "TwitchDownloaderCore\Resources\chat-template.html"
    "TwitchDownloaderCore\Resources\InterVariable.ttf"
    "TwitchDownloaderCore\Resources\InterVariable-Italic.ttf"
    "TwitchDownloaderCore\Extensions\M3U8Extensions.cs"
    "TwitchDownloaderCore\Models\M3U8.cs"
    "TwitchDownloaderCore\Models\M3U8Parse.cs"
    "TwitchDownloaderCore\Models\M3U8VideoQualities.cs"
    "TwitchDownloaderCore\Models\M3U8VideoQuality.cs"
    "TwitchDownloaderCore\Models\ClipVideoQualities.cs"
    "TwitchDownloaderCore\Models\ClipVideoQuality.cs"
    "TwitchDownloaderCore\Models\VideoQualities.cs"
    "TwitchDownloaderCore\Models\Resolution.cs"
    "TwitchDownloaderCore\Models\ThrottledStream.cs"
    "TwitchDownloaderCore\Models\Interfaces\IVideoQualities.cs"
    "TwitchDownloaderCore\Models\Interfaces\IVideoQuality.cs"
    "TwitchDownloaderCore\Tools\ClipQualityComparer.cs"
    "TwitchDownloaderCore\Tools\ClipVideoQualityComparer.cs"
    "TwitchDownloaderCore\Tools\CommentIdEqualityComparer.cs"
    "TwitchDownloaderCore\Tools\DownloadTools.cs"
    "TwitchDownloaderCore\Tools\FfmpegConcatList.cs"
    "TwitchDownloaderCore\Tools\FfmpegMetadata.cs"
    "TwitchDownloaderCore\Tools\IdParse.cs"
    "TwitchDownloaderCore\Tools\M3U8StreamQualityComparer.cs"
    "TwitchDownloaderCore\Tools\VideoSizeEstimator.cs"
    "TwitchDownloaderCore\TwitchObjects\Api\UnavailableMedia.cs"
    "TwitchDownloaderCore\TwitchObjects\Gql\GqlClipResponse.cs"
    "TwitchDownloaderCore\TwitchObjects\Gql\GqlClipSearchResponse.cs"
    "TwitchDownloaderCore\TwitchObjects\Gql\GqlClipTokenResponse.cs"
    "TwitchDownloaderCore\TwitchObjects\Gql\GqlCommentResponse.cs"
    "TwitchDownloaderCore\TwitchObjects\Gql\GqlShareClipRenderStatusResponse.cs"
    "TwitchDownloaderCore\TwitchObjects\Gql\GqlUserIdResponse.cs"
    "TwitchDownloaderCore\TwitchObjects\Gql\GqlVideoChapterResponse.cs"
    "TwitchDownloaderCore\TwitchObjects\Gql\GqlVideoResponse.cs"
    "TwitchDownloaderCore\TwitchObjects\Gql\GqlVideoSearchResponse.cs"
    "TwitchDownloaderCore\TwitchObjects\Gql\GqlVideoTokenResponse.cs"
    "TwitchDownloaderCore.Tests\ModelTests\M3U8Tests.cs"
    "TwitchDownloaderCore.Tests\ModelTests\M3U8VideoQualitiesTests.cs"
    "TwitchDownloaderCore.Tests\ToolTests\IdParseTests.cs"
) do (
    if exist "%~dp0%%~F" del /f /q "%~dp0%%~F" >nul 2>nul
)
if exist "%~dp0TwitchDownloaderCore\Models\Interfaces" rd /s /q "%~dp0TwitchDownloaderCore\Models\Interfaces" >nul 2>nul

echo [1/2] Publishing ChatRenderCLI.exe...
dotnet publish "%~dp0TwitchDownloaderCLI\TwitchDownloaderCLI.csproj" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=none ^
    -p:DebugSymbols=false ^
    -o "%~dp0dist\win-x64"

if %ERRORLEVEL% neq 0 (
    echo.
    echo [ERROR] CLI build failed with exit code %ERRORLEVEL%.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [2/2] Publishing ChatRenderGUI.exe (WPF)...
dotnet publish "%~dp0TwitchDownloaderWPF\TwitchDownloaderWPF.csproj" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:IncludeAllContentForSelfExtract=true ^
    -p:DebugType=none ^
    -p:DebugSymbols=false ^
    -o "%~dp0dist\win-x64"

if %ERRORLEVEL% neq 0 (
    echo.
    echo [ERROR] GUI build failed with exit code %ERRORLEVEL%.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [SUCCESS] Built standalone executables:
echo   %~dp0dist\win-x64\ChatRenderCLI.exe
echo   %~dp0dist\win-x64\ChatRenderGUI.exe
echo.
pause
