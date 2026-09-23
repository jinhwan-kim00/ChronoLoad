@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

rem ChronoLoad 배포 패키지를 만든다.
rem
rem   build-release            자체 포함(기본). 받는 사람이 .NET 을 설치하지 않아도 된다
rem   build-release framework  런타임 의존. 용량은 작지만 .NET 10 데스크톱 런타임이 필요하다
rem
rem 결과: dist\ChronoLoad-<버전>-<종류>.zip  (실행 파일 + 브리지 + README)

cd /d "%~dp0"

set MODE=%1
if "%MODE%"=="" set MODE=selfcontained

rem 단일 파일 압축은 자체 포함일 때만 된다(NETSDK1176).
if /i "%MODE%"=="selfcontained" (
    set SC=--self-contained true
    set COMPRESS=-p:EnableCompressionInSingleFile=true
    set TAG=win-x64
) else if /i "%MODE%"=="framework" (
    set SC=--self-contained false
    set COMPRESS=
    set TAG=win-x64-fx
) else (
    echo 알 수 없는 옵션: %MODE%
    echo 사용법: build-release [selfcontained^|framework]
    exit /b 1
)

for /f "delims=" %%V in ('powershell -NoProfile -Command ^
    "([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version"') do set VERSION=%%V
if "%VERSION%"=="" set VERSION=0.0.0

set STAGE=dist\stage
set OUTPUT=dist\ChronoLoad-%VERSION%-%TAG%.zip

echo.
echo   ChronoLoad %VERSION%  (%MODE%)
echo.

rem 만들 것만 지운다. dist 를 통째로 비우면 앞서 만든 다른 패키지까지 날아간다.
if exist "%STAGE%" rmdir /s /q "%STAGE%"
if exist "%OUTPUT%" del /q "%OUTPUT%"
mkdir "%STAGE%" || exit /b 1

rem 단일 파일로 묶는다. 자체 포함일 때는 네이티브 라이브러리까지 넣어야 진짜 한 개가 된다.
echo [1/3] 앱 빌드
dotnet publish src\ChronoLoad.App\ChronoLoad.App.csproj ^
    -c Release -r win-x64 %SC% ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    %COMPRESS% ^
    -p:DebugType=none ^
    -o "%STAGE%\app" --nologo -v q || exit /b 1

rem MCP 브리지. 이것이 없으면 README 의 MCP 절을 따라할 수 없다.
echo [2/3] MCP 브리지 빌드
dotnet publish tools\ChronoLoad.McpBridge\ChronoLoad.McpBridge.csproj ^
    -c Release -r win-x64 %SC% ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    %COMPRESS% ^
    -p:DebugType=none ^
    -o "%STAGE%\bridge" --nologo -v q || exit /b 1

echo [3/3] 압축
copy /y "%STAGE%\app\ChronoLoad.App.exe" "%STAGE%\ChronoLoad.exe" >nul || exit /b 1
copy /y "%STAGE%\bridge\chronoload-mcp.exe" "%STAGE%\chronoload-mcp.exe" >nul || exit /b 1
copy /y README.md "%STAGE%\README.md" >nul || exit /b 1
rmdir /s /q "%STAGE%\app" "%STAGE%\bridge"

powershell -NoProfile -Command ^
    "Compress-Archive -Path '%STAGE%\*' -DestinationPath '%OUTPUT%' -Force" || exit /b 1
rmdir /s /q "%STAGE%"

for %%F in ("%OUTPUT%") do set SIZE=%%~zF
set /a SIZE_MB=%SIZE% / 1048576

echo.
echo   완료: %OUTPUT%  (%SIZE_MB% MB)
echo.
endlocal
