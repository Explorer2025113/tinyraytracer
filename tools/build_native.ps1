# Build RaytracerNative.dll for Unity (x64). Requires CMake and a C++ toolchain (VS Build Tools).
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$native = Join-Path $root "native"
$out = Join-Path $root "build\native"
$dllDest = Join-Path $root "UnityProject\Assets\Plugins\x86_64\RaytracerNative.dll"

New-Item -ItemType Directory -Force -Path $out | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path $dllDest) | Out-Null

Push-Location $out
try {
    cmake -G "Visual Studio 17 2022" -A x64 $native
    cmake --build . --config Release
    $built = Join-Path $out "Release\RaytracerNative.dll"
    if (-not (Test-Path $built)) {
        $built = Join-Path $out "RaytracerNative.dll"
    }
    if (-not (Test-Path $built)) {
        throw "DLL not found after build. Expected under $out"
    }
    Copy-Item -Force $built $dllDest
    Write-Host "Copied to $dllDest"

    $cgDll = "E:\Unity project\CG Project\Assets\Plugins\x86_64\RaytracerNative.dll"
    if (Test-Path (Split-Path $cgDll)) {
        try {
            Copy-Item -Force $built $cgDll
            Write-Host "Copied to $cgDll (CG Project)"
        } catch {
            Write-Warning "Could not copy to CG Project (close Unity if the DLL is locked): $_"
        }
    }
}
finally {
    Pop-Location
}
