@echo off
setlocal enabledelayedexpansion

set "ROOT_DIR=%~dp0"
if "%ROOT_DIR:~-1%"=="\" set "ROOT_DIR=%ROOT_DIR:~0,-1%"
set "STATE_FILE=%ROOT_DIR%\.installer_state.env"
set "HEALTH_URL=http://localhost:5107/"
set "HOST_MOUNT_OVERRIDE_FILE=docker-compose.host-mounts.generated.yml"
set "ROCM_RUNTIME_OVERRIDE_FILE=docker-compose.rocm-runtime.generated.yml"
set "VOICE_PACK_OVERRIDE_FILE=docker-compose.voice-pack.local.yml"
set "START_COMMAND=start_windows.cmd"
set "ENV_FILE=.env"

set "MODE=install"
set "FIX_MODE=0"
set "INSTALLER_MODE=0"
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
if /I "%~1"=="--installer" (
  set "INSTALLER_MODE=1"
  shift
  goto parse_args
)
if /I "%~1"=="--help" goto usage
if /I "%~1"=="-h" goto usage
call :fail Unknown option: %~1

:args_done
if "%INSTALLER_MODE%"=="1" (
  set "DOCKER_DIR=%ROOT_DIR%\installer\docker"
  set "DOCKER_DIRECTORY=installer/docker"
) else (
  set "DOCKER_DIR=%ROOT_DIR%\docker"
  set "DOCKER_DIRECTORY=docker"
)
if not exist "%DOCKER_DIR%" call :fail Docker directory not found: %DOCKER_DIR%

if /I not "%COMPOSE_MODE%"=="ghcr" if /I not "%COMPOSE_MODE%"=="local" call :fail --compose must be ghcr or local
if not "%BACKEND_OVERRIDE%"=="" (
  if /I not "%BACKEND_OVERRIDE%"=="cpu" if /I not "%BACKEND_OVERRIDE%"=="cuda13" if /I not "%BACKEND_OVERRIDE%"=="rocm" if /I not "%BACKEND_OVERRIDE%"=="slim" if /I not "%BACKEND_OVERRIDE%"=="vulkan" call :fail --backend must be cpu, cuda13, rocm, slim, or vulkan
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
call :validate_backend
call :select_compose_file
call :select_vulkan_runtime
call :select_rocm_runtime

if "%INSTALLER_MODE%"=="1" (
  call :log Installer layout: using %DOCKER_DIRECTORY% for compose, volumes, and overrides.
)
call :log Selected backend: %SELECTED_BACKEND%
call :log Compose file: %DOCKER_DIRECTORY%\%COMPOSE_FILE%

if /I "%MODE%"=="doctor" (
  call :log Doctor mode complete. No changes were made.
  call :save_state
  exit /b 0
)

pushd "%DOCKER_DIR%" || call :fail Could not open docker directory.
set "COMPOSE_ARGS=-f %COMPOSE_FILE%"
if exist "%HOST_MOUNT_OVERRIDE_FILE%" (
  docker compose -f "%COMPOSE_FILE%" -f "%HOST_MOUNT_OVERRIDE_FILE%" --env-file "%ENV_FILE%" config >nul 2>nul
  if errorlevel 1 (
    call :warn Ignoring invalid host mount override %DOCKER_DIRECTORY%\%HOST_MOUNT_OVERRIDE_FILE%. Recreate mounts to regenerate it.
  ) else (
    set "COMPOSE_ARGS=%COMPOSE_ARGS% -f %HOST_MOUNT_OVERRIDE_FILE%"
    call :log Including host mount override: %HOST_MOUNT_OVERRIDE_FILE%
  )
)
if exist "%ROCM_RUNTIME_OVERRIDE_FILE%" (
  docker compose -f "%COMPOSE_FILE%" -f "%ROCM_RUNTIME_OVERRIDE_FILE%" --env-file "%ENV_FILE%" config >nul 2>nul
  if errorlevel 1 (
    call :warn Ignoring invalid ROCm runtime override %DOCKER_DIRECTORY%\%ROCM_RUNTIME_OVERRIDE_FILE%.
  ) else (
    set "COMPOSE_ARGS=%COMPOSE_ARGS% -f %ROCM_RUNTIME_OVERRIDE_FILE%"
    call :log Including ROCm runtime override: %ROCM_RUNTIME_OVERRIDE_FILE%
  )
)
if exist "%VOICE_PACK_OVERRIDE_FILE%" (
  docker compose -f "%COMPOSE_FILE%" -f "%VOICE_PACK_OVERRIDE_FILE%" --env-file "%ENV_FILE%" config >nul 2>nul
  if errorlevel 1 (
    call :warn Ignoring invalid voice pack override %DOCKER_DIRECTORY%\%VOICE_PACK_OVERRIDE_FILE%.
  ) else (
    set "COMPOSE_ARGS=%COMPOSE_ARGS% -f %VOICE_PACK_OVERRIDE_FILE%"
    call :log Including voice pack override: %VOICE_PACK_OVERRIDE_FILE%
  )
)
docker compose %COMPOSE_ARGS% --env-file "%ENV_FILE%" up -d || (
  popd
  call :fail docker compose up failed.
)
popd

call :wait_for_health
if errorlevel 1 (
  call :warn GuideAnts did not pass health check in time. Check: docker compose -f %DOCKER_DIRECTORY%\%COMPOSE_FILE% ps
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

:validate_backend
if /I not "%SELECTED_BACKEND%"=="cuda13" exit /b 0

set "NVIDIA_DRIVER_VERSION="
for /f "delims=" %%a in ('nvidia-smi --query-gpu^=driver_version --format^=csv^,noheader 2^>nul') do (
  if not defined NVIDIA_DRIVER_VERSION set "NVIDIA_DRIVER_VERSION=%%a"
)

if not defined NVIDIA_DRIVER_VERSION (
  if not "%BACKEND_OVERRIDE%"=="" (
    call :fail Could not read NVIDIA driver version from nvidia-smi. Remove --backend cuda13 or fix NVIDIA driver/runtime.
  )
  call :warn Could not read NVIDIA driver version from nvidia-smi. Falling back to vulkan backend.
  set "SELECTED_BACKEND=vulkan"
  exit /b 0
)

for /f "tokens=1 delims=." %%a in ("%NVIDIA_DRIVER_VERSION%") do set "NVIDIA_DRIVER_MAJOR=%%a"
if not defined NVIDIA_DRIVER_MAJOR set "NVIDIA_DRIVER_MAJOR=0"
set /a NVIDIA_DRIVER_MAJOR_NUM=%NVIDIA_DRIVER_MAJOR% >nul 2>nul
if errorlevel 1 (
  if not "%BACKEND_OVERRIDE%"=="" (
    call :fail Could not parse NVIDIA driver version "%NVIDIA_DRIVER_VERSION%". Remove --backend cuda13 or fix NVIDIA drivers.
    exit /b 1
  )
  call :warn Could not parse NVIDIA driver version "%NVIDIA_DRIVER_VERSION%". Falling back to vulkan backend.
  set "SELECTED_BACKEND=vulkan"
  exit /b 0
)

rem CUDA 13 requires NVIDIA R580+ drivers.
if %NVIDIA_DRIVER_MAJOR_NUM% LSS 580 (
  if not "%BACKEND_OVERRIDE%"=="" (
    call :fail NVIDIA driver %NVIDIA_DRIVER_VERSION% is too old for cuda13. Install R580+ driver or use --backend cpu.
    exit /b 1
  )
  call :warn NVIDIA driver %NVIDIA_DRIVER_VERSION% is below the CUDA 13 minimum ^(R580^). Falling back to vulkan backend.
  set "SELECTED_BACKEND=vulkan"
  exit /b 0
)

call :log NVIDIA driver %NVIDIA_DRIVER_VERSION% satisfies CUDA 13 minimum ^(R580^).
exit /b 0

:select_compose_file
if /I "%COMPOSE_MODE%"=="local" (
  if /I "%SELECTED_BACKEND%"=="slim" (
    set "COMPOSE_FILE=docker-compose.slim.yml"
  ) else if /I "%SELECTED_BACKEND%"=="cuda13" (
    set "COMPOSE_FILE=docker-compose.cuda.yml"
  ) else if /I "%SELECTED_BACKEND%"=="rocm" (
    set "COMPOSE_FILE=docker-compose.rocm.yml"
  ) else if /I "%SELECTED_BACKEND%"=="vulkan" (
    set "COMPOSE_FILE=docker-compose.vulkan.yml"
  ) else (
    set "COMPOSE_FILE=docker-compose.cpu.yml"
  )
) else (
  if /I "%SELECTED_BACKEND%"=="slim" (
    set "COMPOSE_FILE=docker-compose.ghcr-slim.yml"
  ) else if /I "%SELECTED_BACKEND%"=="cuda13" (
    set "COMPOSE_FILE=docker-compose.ghcr-cuda13.yml"
  ) else if /I "%SELECTED_BACKEND%"=="rocm" (
    set "COMPOSE_FILE=docker-compose.ghcr-rocm.yml"
  ) else if /I "%SELECTED_BACKEND%"=="vulkan" (
    set "COMPOSE_FILE=docker-compose.ghcr-vulkan.yml"
  ) else (
    set "COMPOSE_FILE=docker-compose.ghcr-cpu.yml"
  )
)
exit /b 0

:select_vulkan_runtime
if /I not "%SELECTED_BACKEND%"=="vulkan" exit /b 0
call :log Vulkan: Docker Desktop -^> Mesa dzn over D3D12 (/dev/dxg). Using built-in defaults (no env).
exit /b 0

:select_rocm_runtime
if /I not "%SELECTED_BACKEND%"=="rocm" exit /b 0
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT_DIR%\installer\scripts\rocm-runtime-compose.ps1" -DockerDir "%DOCKER_DIR%" -Backend "%SELECTED_BACKEND%"
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
  echo COMPOSE_FILE=%COMPOSE_FILE%
  echo HOST_MOUNT_OVERRIDE_FILE=%HOST_MOUNT_OVERRIDE_FILE%
  echo VOICE_PACK_OVERRIDE_FILE=%VOICE_PACK_OVERRIDE_FILE%
  echo DOCKER_DIRECTORY=%DOCKER_DIRECTORY%
  echo START_COMMAND=%START_COMMAND%
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
echo   --backend cpu^|cuda13^|rocm^|slim^|vulkan   Force backend selection. slim and vulkan are explicit only and are not auto-detected.
echo   --compose ghcr^|local   Use GHCR compose files ^(default^) or local build files.
echo   --installer            Use installer/docker compose files, volumes, and overrides.
echo   --help                 Show this help.
exit /b 0
