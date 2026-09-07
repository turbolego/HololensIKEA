$ErrorActionPreference = 'Stop'

$source = Join-Path $env:RUNNER_TEMP 'evergine-draco'
$build = Join-Path $env:RUNNER_TEMP 'evergine-draco-build'
$output = Join-Path $env:GITHUB_WORKSPACE 'draco_tiny_dec.dll'

if (Test-Path $source) { Remove-Item -Recurse -Force $source }
if (Test-Path $build) { Remove-Item -Recurse -Force $build }

git clone --depth 1 https://github.com/EvergineTeam/draco.git $source

cmake -S $source -B $build `
  -DDRACO_TINY_LIB=ON `
  -DDRACO_TINY_LIB_SHARED=ON `
  -DCMAKE_SYSTEM_NAME=WindowsStore `
  '-DCMAKE_SYSTEM_VERSION=10.0.19041.0' `
  -DCMAKE_C_COMPILER_WORKS=FALSE `
  -A Win32
if ($LASTEXITCODE -ne 0) { throw "CMake UWP configuration failed with exit code $LASTEXITCODE" }

cmake --build $build --config Release --target draco_tiny_dec
if ($LASTEXITCODE -ne 0) { throw "CMake UWP build failed with exit code $LASTEXITCODE" }

$built = Join-Path $build 'Release\draco_tiny_dec.dll'
if (-not (Test-Path $built)) {
  throw "UWP Draco build did not produce $built"
}

Copy-Item -Force $built $output
Write-Host "Built UWP x86 Draco decoder: $output"
