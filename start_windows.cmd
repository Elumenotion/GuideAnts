@echo off
setlocal enabledelayedexpansion

set "ROOT_DIR=%~dp0"
if "%ROOT_DIR:~-1%"=="\" set "ROOT_DIR=%ROOT_DIR:~0,-1%"
set "DOCKER_DIR=%ROOT_DIR%\docker"
set "STATE_FILE=%ROOT_DIR%\.installer_state.env"
set "HEALTH_URL=http://localhost:5107/"

set "MODE=install"
set "FIX_MODE=0"
set "BACKEND_OVERRIDE="
set "COMPOSE_MODE=ghcr"

:parse_args
if "%~1"=="" goto args_done
if /I "%~1"=="--doctor" (
  set "MODE=doctor"
  shift
  goto parse_args
)
if /I "%~1"=="--fix" (
  set "FIX_MODE=1"
  shift
  goto parse_args
)
if /I "%~1"=="--backend" (
  if "%~2"=="" call :fail Missing value for --backend
  set "BACKEND_OVERRIDE=%~2"
  shift
  shift
  goto parse_args
)
if /I "%~1"=="--compose" (
  if "%~2"=="" call :fail Missing value for --compose
  set "COMPOSE_MODE=%~2"
  shift
  shift
  goto parse_args
)
if /I "%~1"=="--help" goto usage
if /I "%~1"=="-h" goto usage
call :fail Unknown option: %~1

:args_done
if /I not "%COMPOSE_MODE%"=="ghcr" if /I not "%COMPOSE_MODE%"=="local" call :fail --compose must be ghcr or local
if not "%BACKEND_OVERRIDE%"=="" (
  if /I not "%BACKEND_OVERRIDE%"=="cpu" if /I not "%BACKEND_OVERRIDE%"=="cuda13" if /I not "%BACKEND_OVERRIDE%"=="rocm" call :fail --backend must be cpu, cuda13, or rocm
)

call :log Running preflight checks...
where docker >nul 2>nul || (
  if "%FIX_MODE%"=="1" (
    call :warn Docker CLI missing. Attempting winget install for Docker Desktop...
    where winget >nul 2>nul && winget install -e --id Docker.DockerDesktop
  )
  where docker >nul 2>nul || call :fail Docker CLI not found.
)

docker compose version >nul 2>nul || call :fail Docker Compose plugin not found (docker compose).
docker info >nul 2>nul || call :fail Docker daemon is not reachable. Start Docker Desktop and rerun.

call :check_wsl

call :detect_backend
call :select_compose_file

call :log Selected backend: %SELECTED_BACKEND%
call :log Compose file: docker\%COMPOSE_FILE%

if /I "%MODE%"=="doctor" (
  call :log Doctor mode complete. No changes were made.
  call :save_state
  exit /b 0
)

pushd "%DOCKER_DIR%" || call :fail Could not open docker directory.
docker compose -f "%COMPOSE_FILE%" up -d || (
  popd
  call :fail docker compose up failed.
)
popd

call :wait_for_health
if errorlevel 1 (
  call :warn GuideAnts did not pass health check in time. Check: docker compose -f docker\%COMPOSE_FILE% ps
) else (
  call :log GuideAnts is up: %HEALTH_URL%
  start "" "%HEALTH_URL%"
)

call :save_state
exit /b 0

:check_wsl
wsl --status >nul 2>nul
if errorlevel 1 (
  if "%FIX_MODE%"=="1" (
    call :warn WSL is not available. Attempting install...
    wsl --install >nul 2>nul
  )
  wsl --status >nul 2>nul || call :warn WSL check failed. Docker Desktop may still work if configured differently.
  exit /b 0
)

for /f "tokens=1,* delims=:" %%a in ('wsl --status 2^>nul ^| findstr /I "Default Version"') do (
  set "WSL_DEFAULT_VERSION=%%b"
)
if defined WSL_DEFAULT_VERSION (
  echo !WSL_DEFAULT_VERSION! | findstr /R "2" >nul || call :warn WSL default version is not 2.
)
exit /b 0

:detect_backend
if not "%BACKEND_OVERRIDE%"=="" (
  set "SELECTED_BACKEND=%BACKEND_OVERRIDE%"
  exit /b 0
)

where nvidia-smi >nul 2>nul || (
  goto detect_amd
)

nvidia-smi >nul 2>nul || (
  goto detect_amd
)

set "SELECTED_BACKEND=cuda13"
exit /b 0

:detect_amd
for /f "delims=" %%a in ('powershell -NoProfile -Command "$g=(Get-CimInstance Win32_VideoController 2>$null | Select-Object -ExpandProperty Name) -join ''`n''; if($g -match ''AMD|Radeon''){''rocm''} else {''cpu''}"') do set "SELECTED_BACKEND=%%a"
if not defined SELECTED_BACKEND set "SELECTED_BACKEND=cpu"
exit /b 0

:select_compose_file
if /I "%COMPOSE_MODE%"=="local" (
  if /I "%SELECTED_BACKEND%"=="cuda13" (
    set "COMPOSE_FILE=docker-compose.cuda.yml"
  ) else if /I "%SELECTED_BACKEND%"=="rocm" (
    set "COMPOSE_FILE=docker-compose.rocm.yml"
  ) else (
    set "COMPOSE_FILE=docker-compose.cpu.yml"
  )
) else (
  if /I "%SELECTED_BACKEND%"=="cuda13" (
    set "COMPOSE_FILE=docker-compose.ghcr-cuda13.yml"
  ) else if /I "%SELECTED_BACKEND%"=="rocm" (
    set "COMPOSE_FILE=docker-compose.ghcr-rocm.yml"
  ) else (
    set "COMPOSE_FILE=docker-compose.ghcr-cpu.yml"
  )
)
exit /b 0

:wait_for_health
set /a _max=120
set /a _count=0
:wait_loop
set /a _count+=1
curl -fsS "%HEALTH_URL%" >nul 2>nul && exit /b 0
if %_count% GEQ %_max% exit /b 1
timeout /t 2 /nobreak >nul
goto wait_loop

:save_state
(
  echo BACKEND=%SELECTED_BACKEND%
  echo COMPOSE_MODE=%COMPOSE_MODE%
  for /f %%i in ('powershell -NoProfile -Command "[int][double]::Parse((Get-Date -UFormat %%s))"') do echo LAST_RUN_EPOCH=%%i
) > "%STATE_FILE%"
exit /b 0

:log
echo [guideants-installer] %*
exit /b 0

:warn
echo [guideants-installer][warn] %* 1>&2
exit /b 0

:fail
echo [guideants-installer][error] %* 1>&2
exit /b 1

:usage
echo Usage: start_windows.cmd [options]
echo.
echo Options:
echo   --doctor               Run checks only, do not change anything.
echo   --fix                  Attempt limited auto-remediation where possible.
echo   --backend cpu^|cuda13^|rocm   Force backend selection.
echo   --compose ghcr^|local   Use GHCR compose files ^(default^) or local build files.
echo   --help                 Show this help.
exit /b 0
