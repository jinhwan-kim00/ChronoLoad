@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

rem ChronoLoad 배포 패키지를 만든다.
rem
rem   build-release            자체 포함(기본). 받는 사람이 .NET 을 설치하지 않아도 된다
rem   build-release framework  런타임 의존. 용량은 작지만 .NET 10 데스크톱 런타임이 필요하다
rem
rem 결과: dist\ChronoLoad-<버전>-<종류>.zip  (실행 파일 + 브리지 + README + 개정 이력 + 스크린샷)

cd /d "%~dp0"

rem 탐색기에서 더블클릭하면 cmd /c 로 실행되어 스크립트가 끝나는 순간 창이 닫힌다.
rem 실패 메시지를 읽을 틈이 없어 "1/3 에서 멈췄다"로만 보인다. 그때만 마지막에 멈춰 선다.
set "KEEPOPEN="
echo %cmdcmdline% | findstr /i /c:"%~nx0" >nul && set "KEEPOPEN=1"

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
    if defined KEEPOPEN pause
    exit /b 1
)

for /f "delims=" %%V in ('powershell -NoProfile -Command ^
    "([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version"') do set VERSION=%%V
if "%VERSION%"=="" set VERSION=0.0.0

rem 푼 폴더를 결과물로 남긴다. 압축본만 내면 이 PC 에서 바로 쓰려는 사람이 매번 풀어야 하고,
rem 바로가기를 걸 자리도 없다. 폴더 이름은 zip 과 같은 줄기라 무엇을 실행 중인지 알 수 있다.
set STAGE=dist\ChronoLoad-%VERSION%-%TAG%
set OUTPUT=%STAGE%.zip

echo.
echo   ChronoLoad %VERSION%  (%MODE%)
echo   자체 포함 빌드는 단계마다 1~2분 걸린다. 출력이 멎어 보여도 진행 중이다.
echo.

rem 만들 것만 지운다. dist 를 통째로 비우면 앞서 만든 다른 패키지까지 날아간다.
set STEP=준비
if exist "%STAGE%" rmdir /s /q "%STAGE%"
if exist "%OUTPUT%" del /q "%OUTPUT%"

rem 지워지지 않았다면 그 폴더에서 앱이 돌고 있을 가능성이 높다. 바로가기로 쓰기 시작하면
rem 흔한 상황이라, 뒤에서 엉뚱한 오류로 터지기 전에 여기서 분명히 말한다.
if exist "%STAGE%" (
    set STEP=이전 결과 폴더를 지울 수 없음
    echo.
    echo   %STAGE% 를 지울 수 없다. 그 폴더의 ChronoLoad.exe 가 실행 중인지 확인한다.
    goto :fail
)

mkdir "%STAGE%" || goto :fail

rem 단일 파일로 묶는다. 자체 포함일 때는 네이티브 라이브러리까지 넣어야 진짜 한 개가 된다.
rem -v m 인 이유: -v q 는 오류 말고 아무것도 내지 않아, 몇 분짜리 단계가 멈춘 것처럼 보인다.
set STEP=[1/3] 앱 빌드
echo %STEP%
dotnet publish src\ChronoLoad.App\ChronoLoad.App.csproj ^
    -c Release -r win-x64 %SC% ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    %COMPRESS% ^
    -p:DebugType=none ^
    -o "%STAGE%\app" --nologo -v m || goto :fail

rem MCP 브리지. 이것이 없으면 README 의 MCP 절을 따라할 수 없다.
set STEP=[2/3] MCP 브리지 빌드
echo %STEP%
dotnet publish tools\ChronoLoad.McpBridge\ChronoLoad.McpBridge.csproj ^
    -c Release -r win-x64 %SC% ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    %COMPRESS% ^
    -p:DebugType=none ^
    -o "%STAGE%\bridge" --nologo -v m || goto :fail

set STEP=[3/3] 압축
echo %STEP%
copy /y "%STAGE%\app\ChronoLoad.App.exe" "%STAGE%\ChronoLoad.exe" >nul || goto :fail
copy /y "%STAGE%\bridge\chronoload-mcp.exe" "%STAGE%\chronoload-mcp.exe" >nul || goto :fail
copy /y README.md "%STAGE%\README.md" >nul || goto :fail
copy /y CHANGE_LOG.md "%STAGE%\CHANGE_LOG.md" >nul || goto :fail

rem README 가 docs\screenshot.png 를 가리킨다. 같이 넣지 않으면 받는 사람의 화면에는
rem 깨진 이미지 자리만 남는다 — 상대 경로 그대로 두려면 폴더 구조도 그대로 만든다.
mkdir "%STAGE%\docs" || goto :fail
copy /y docs\screenshot.png "%STAGE%\docs\screenshot.png" >nul || goto :fail
rmdir /s /q "%STAGE%\app" "%STAGE%\bridge"

rem 푼 폴더는 지우지 않는다. 압축은 그 폴더를 그대로 담는다.
powershell -NoProfile -Command ^
    "Compress-Archive -Path '%STAGE%\*' -DestinationPath '%OUTPUT%' -Force" || goto :fail

for %%F in ("%OUTPUT%") do set SIZE=%%~zF
set /a SIZE_MB=%SIZE% / 1048576

echo.
echo   완료
echo     배포용 : %OUTPUT%  (%SIZE_MB% MB)
echo     실행용 : %STAGE%\ChronoLoad.exe
echo.
echo   이 PC 에서 그냥 쓰려면 위 실행 파일에 바로가기를 만든다.
echo   다시 빌드하면 그 폴더를 지웠다 새로 만드므로, 실행 중이면 먼저 닫는다.
echo.
if defined KEEPOPEN pause
endlocal
exit /b 0

:fail
set CODE=%ERRORLEVEL%
if "%CODE%"=="0" set CODE=1
echo.
echo   실패: %STEP%  (종료 코드 %CODE%)
echo.
echo   앱이 실행 중이면 출력 파일이 잠겨 빌드가 실패한다. 닫고 다시 실행한다.
echo   작업 폴더에 남은 것: %STAGE%
echo.
if defined KEEPOPEN pause
endlocal
exit /b %CODE%
