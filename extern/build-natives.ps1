# Builds miniHawk's native library dependencies from source (Windows x64).
# Invoked as part of the main solution build (BuildNativeDeps target in
# source/MainSlnExecutable.props); can also be run by hand.
#
#   .\build-natives.ps1 -OutDir <repo>\build\dll [-Libs chimerahash,sdl2] [-Force]
#
# Each library is skipped when its output is newer than all of its sources
# (so incremental solution builds stay fast). Requires Visual Studio 2022 with
# the C++ workload including clang-cl, CMake, and Ninja components.
param(
    [Parameter(Mandatory = $true)] [string]$OutDir,
    [string[]]$Libs = @("chimerahash", "sdl2", "lua54", "zstd", "cimgui", "openal", "sqlite3", "chdcapi", "luasocket"),
    [switch]$Force
)
# native tools (cmake, clang) write progress/warnings to stderr; success is judged
# strictly by exit codes below, so stderr must not be promoted to an error here
$ErrorActionPreference = "Continue"
$here = $PSScriptRoot
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force $OutDir | Out-Null

# --- Visual Studio dev environment (cl/clang-cl/cmake/ninja on PATH, CRT lib paths) ---
if (-not (Get-Command clang-cl -ErrorAction SilentlyContinue)) {
    $vsDevCmd = Get-ChildItem "C:\Program Files\Microsoft Visual Studio\*\*\Common7\Tools\VsDevCmd.bat" -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $vsDevCmd) { throw "Visual Studio (VsDevCmd.bat) not found; install VS2022 with the C++ workload" }
    $envDump = cmd /c "`"$($vsDevCmd.FullName)`" -arch=amd64 -no_logo 2>nul && set"
    foreach ($line in $envDump) {
        if ($line -match '^([^=]+)=(.*)$') { Set-Item -Path "env:$($Matches[1])" -Value $Matches[2] }
    }
    if (-not (Get-Command clang-cl -ErrorAction SilentlyContinue)) {
        # clang-cl lives in the VS LLVM dir which VsDevCmd doesn't always add
        $llvm = Get-ChildItem "C:\Program Files\Microsoft Visual Studio\*\*\VC\Tools\Llvm\x64\bin" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $llvm) { $env:PATH = "$($llvm.FullName);$env:PATH" }
    }
    if (-not (Get-Command clang-cl -ErrorAction SilentlyContinue)) { throw "clang-cl not found; install the 'C++ Clang tools for Windows' VS component" }
}
if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) {
    $cm = Get-ChildItem "C:\Program Files\Microsoft Visual Studio\*\*\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $cm) { $env:PATH = "$($cm.FullName);$env:PATH" }
}
if (-not (Get-Command ninja -ErrorAction SilentlyContinue)) {
    $nj = Get-ChildItem "C:\Program Files\Microsoft Visual Studio\*\*\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $nj) { $env:PATH = "$($nj.FullName);$env:PATH" }
}

function Test-Fresh([string]$output, [string[]]$sourceDirs, [string[]]$sourceFiles) {
    if ($Force) { return $false }
    if (-not (Test-Path $output)) { return $false }
    $outTime = (Get-Item $output).LastWriteTimeUtc
    $newest = [datetime]::MinValue
    foreach ($d in $sourceDirs) {
        $t = Get-ChildItem $d -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property LastWriteTimeUtc -Maximum
        if ($null -ne $t.Maximum -and $t.Maximum -gt $newest) { $newest = $t.Maximum }
    }
    foreach ($f in $sourceFiles) {
        if ((Test-Path $f) -and ((Get-Item $f).LastWriteTimeUtc -gt $newest)) { $newest = (Get-Item $f).LastWriteTimeUtc }
    }
    return $outTime -gt $newest
}

# --- libchimerahash: hardware-accelerated hashing (CRC32 PCLMULQDQ + SHA1-NI) ---
# clang-cl (not cl) because the sources use gcc function-target attributes.
function Build-ChimeraHash {
    $src = Join-Path $here "LibChimeraHash"
    $out = Join-Path $OutDir "libchimerahash.dll"
    if (Test-Fresh $out @($src) @()) { "libchimerahash: up to date"; return }
    "libchimerahash: building..."
    $files = @(Get-ChildItem "$src\common\*.c") + @(Get-ChildItem "$src\crc32\*.c") + @(Get-ChildItem "$src\sha1\*.c") + @(Get-Item "$src\bizinterface.c")
    $objDir = Join-Path $src "obj"
    New-Item -ItemType Directory -Force $objDir | Out-Null
    & clang-cl /nologo /O2 /MD /LD ("/I" + (Join-Path $src "common")) `
        ($files | ForEach-Object FullName) `
        /Fo"$objDir\" /Fe"$out" `
        /link /DEF:"$src\libchimerahash.def" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "libchimerahash build failed" }
    # keep only the dll in the output dir
    Remove-Item (Join-Path $OutDir "libchimerahash.lib"), (Join-Path $OutDir "libchimerahash.exp") -ErrorAction SilentlyContinue
    "libchimerahash: ok"
}

# --- SDL2: input + OpenGL context + software 2D renderer ---
# Built from the SDL submodule via the wrapper CMakeLists (the subsystem
# configuration); its POST_BUILD step copies SDL2.dll next to the other dlls.
function Build-SDL2 {
    $src = Join-Path $here "SDL2"
    $out = Join-Path $OutDir "SDL2.dll"
    if (-not (Test-Path "$src\SDL\CMakeLists.txt")) { throw "SDL submodule not initialized (git submodule update --init extern/SDL2/SDL)" }
    if (Test-Fresh $out @("$src\SDL\src", "$src\SDL\include") @("$src\CMakeLists.txt")) { "SDL2: up to date"; return }
    "SDL2: building..."
    $bld = Join-Path $src "build"
    & cmake -S $src -B $bld -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER=clang-cl -DCMAKE_CXX_COMPILER=clang-cl | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "SDL2 cmake configure failed" }
    & cmake --build $bld | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "SDL2 build failed" }
    $built = "$bld\SDL\SDL2.dll"
    if (-not (Test-Path $built)) { throw "SDL2 build produced no SDL2.dll" }
    Copy-Item $built $out -Force
    "SDL2: ok"
}

# --- lua54: the Lua 5.4 runtime (NLua P/Invokes standard lua54.dll exports) ---
function Build-Lua54 {
    $src = Join-Path $here "lua"
    $out = Join-Path $OutDir "lua54.dll"
    if (Test-Fresh $out @($src) @()) { "lua54: up to date"; return }
    "lua54: building..."
    $exclude = @("lua.c", "luac.c", "onelua.c", "ltests.c")
    $files = Get-ChildItem "$src\*.c" | Where-Object { $exclude -notcontains $_.Name }
    $objDir = Join-Path $src "obj"; New-Item -ItemType Directory -Force $objDir | Out-Null
    & clang-cl /nologo /O2 /MD /LD /DLUA_BUILD_AS_DLL ($files | ForEach-Object FullName) `
        /Fo"$objDir\" /Fe"$out" /link "/IMPLIB:$objDir\lua54.lib" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "lua54 build failed" }
    Remove-Item (Join-Path $OutDir "lua54.exp") -ErrorAction SilentlyContinue
    "lua54: ok"
}

# --- libzstd: savestate compression ---
function Build-Zstd {
    $src = Join-Path $here "zstd"
    $out = Join-Path $OutDir "libzstd.dll"
    if (Test-Fresh $out @("$src\lib") @()) { "libzstd: up to date"; return }
    "libzstd: building..."
    $bld = Join-Path $src "build-minihawk"
    & cmake -S "$src\build\cmake" -B $bld -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER=clang-cl -DCMAKE_CXX_COMPILER=clang-cl `
        -DZSTD_BUILD_PROGRAMS=OFF -DZSTD_BUILD_STATIC=OFF -DZSTD_BUILD_SHARED=ON -DZSTD_BUILD_TESTS=OFF -DZSTD_LEGACY_SUPPORT=OFF | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "zstd configure failed" }
    & cmake --build $bld | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "zstd build failed" }
    $built = Get-ChildItem "$bld\lib\zstd.dll" -ErrorAction SilentlyContinue
    if ($null -eq $built) { throw "zstd build produced no zstd.dll" }
    Copy-Item $built.FullName $out -Force
    "libzstd: ok"
}

# --- cimgui: Dear ImGui C bindings (pinned to the tag matching ImGui.NET) ---
function Build-CImGui {
    $src = Join-Path $here "cimgui"
    $out = Join-Path $OutDir "cimgui.dll"
    if (-not (Test-Path "$src\imgui\imgui.cpp")) { throw "cimgui's imgui submodule not initialized (git submodule update --init --recursive extern/cimgui)" }
    if (Test-Fresh $out @($src) @()) { "cimgui: up to date"; return }
    "cimgui: building..."
    $bld = Join-Path $src "build-minihawk"
    & cmake -S $src -B $bld -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER=clang-cl -DCMAKE_CXX_COMPILER=clang-cl `
        -DIMGUI_STATIC=no | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "cimgui configure failed" }
    & cmake --build $bld | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "cimgui build failed" }
    $built = Get-ChildItem "$bld\cimgui.dll" -ErrorAction SilentlyContinue
    if ($null -eq $built) { throw "cimgui build produced no cimgui.dll" }
    Copy-Item $built.FullName $out -Force
    "cimgui: ok"
}

# --- OpenAL32: the OpenAL Soft audio backend ---
function Build-OpenAL {
    $src = Join-Path $here "openal-soft"
    $out = Join-Path $OutDir "OpenAL32.dll"
    if (Test-Fresh $out @("$src\core", "$src\al", "$src\alc") @("$src\CMakeLists.txt")) { "OpenAL32: up to date"; return }
    "OpenAL32: building..."
    $bld = Join-Path $src "build-minihawk"
    & cmake -S $src -B $bld -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER=clang-cl -DCMAKE_CXX_COMPILER=clang-cl `
        -DALSOFT_UTILS=OFF -DALSOFT_EXAMPLES=OFF -DALSOFT_TESTS=OFF -DLIBTYPE=SHARED | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "openal configure failed" }
    & cmake --build $bld | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "openal build failed" }
    $built = Get-ChildItem "$bld\OpenAL32.dll" -ErrorAction SilentlyContinue
    if ($null -eq $built) { throw "openal build produced no OpenAL32.dll" }
    Copy-Item $built.FullName $out -Force
    "OpenAL32: ok"
}

# --- e_sqlite3: SQLite with the SQLitePCLRaw configuration (Lua SQL API) ---
# Defines mirror ericsink/cb's write_e_sqlite3 generator (the canonical e_sqlite3).
function Build-Sqlite3 {
    $src = Join-Path $here "sqlite3\sqlite3"
    $out = Join-Path $OutDir "e_sqlite3.dll"
    if (Test-Fresh $out @() @("$src\sqlite3.c", "$src\sqlite3.h")) { "e_sqlite3: up to date"; return }
    "e_sqlite3: building..."
    $objDir = Join-Path $here "sqlite3\obj"; New-Item -ItemType Directory -Force $objDir | Out-Null
    & clang-cl /nologo /O2 /MD /LD `
        "/DSQLITE_API=__declspec(dllexport)" `
        /DSQLITE_ENABLE_COLUMN_METADATA /DSQLITE_ENABLE_FTS3_PARENTHESIS /DSQLITE_ENABLE_FTS4 /DSQLITE_ENABLE_FTS5 `
        /DSQLITE_ENABLE_JSON1 /DSQLITE_ENABLE_MATH_FUNCTIONS /DSQLITE_ENABLE_RTREE /DSQLITE_ENABLE_SNAPSHOT `
        /DSQLITE_DEFAULT_FOREIGN_KEYS=1 `
        "$src\sqlite3.c" /Fo"$objDir\" /Fe"$out" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "e_sqlite3 build failed" }
    Remove-Item (Join-Path $OutDir "e_sqlite3.lib"), (Join-Path $OutDir "e_sqlite3.exp") -ErrorAction SilentlyContinue
    "e_sqlite3: ok"
}

# --- chd_capi: CHD disc image reading (Rust; used by DiscSystem) ---
function Build-ChdCapi {
    $src = Join-Path $here "libchd-rs-capi"
    $out = Join-Path $OutDir "chd_capi.dll"
    if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
        $cargoBin = Join-Path $env:USERPROFILE ".cargo\bin"
        if (Test-Path "$cargoBin\cargo.exe") { $env:PATH = "$cargoBin;$env:PATH" }
    }
    if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
        throw "cargo not found - install Rust via 'winget install Rustlang.Rustup' then 'rustup default stable-x86_64-pc-windows-msvc'"
    }
    if (Test-Fresh $out @("$src\src") @("$src\Cargo.toml", "$src\Cargo.lock")) { "chd_capi: up to date"; return }
    "chd_capi: building..."
    Push-Location $src
    try {
        & cargo build --release | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "chd_capi build failed" }
    }
    finally { Pop-Location }
    Copy-Item "$src\target\release\chd_capi.dll" $out -Force
    "chd_capi: ok"
}

# --- luasocket: socket/core.dll + mime/core.dll for user Lua scripts ---
# Linked against our own lua54 import library, so lua54 must build first.
function Build-LuaSocket {
    $src = Join-Path $here "luasocket\src"
    $luaObj = Join-Path $here "lua\obj"
    $luaDir = [System.IO.Path]::GetFullPath((Join-Path $OutDir "..\Lua"))
    $outSocket = Join-Path $luaDir "socket\core.dll"
    $outMime = Join-Path $luaDir "mime\core.dll"
    if (-not (Test-Path "$luaObj\lua54.lib")) { throw "lua54 import library missing; build lua54 first" }
    if ((Test-Fresh $outSocket @($src) @()) -and (Test-Fresh $outMime @($src) @())) { "luasocket: up to date"; return }
    "luasocket: building..."
    New-Item -ItemType Directory -Force (Split-Path $outSocket), (Split-Path $outMime) | Out-Null
    $objDir = Join-Path $here "luasocket\obj"; New-Item -ItemType Directory -Force $objDir | Out-Null
    $socketSrcs = @("luasocket.c","timeout.c","buffer.c","io.c","auxiliar.c","options.c","inet.c","except.c","select.c","tcp.c","udp.c","compat.c","wsocket.c") | ForEach-Object { Join-Path $src $_ }
    & clang-cl /nologo /O2 /MD /LD ("/I" + (Join-Path $here "lua")) `
        "/DLUASOCKET_API=__declspec(dllexport)" `
        $socketSrcs /Fo"$objDir\" /Fe"$outSocket" /link "$luaObj\lua54.lib" ws2_32.lib | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "luasocket (socket) build failed" }
    $mimeSrcs = @("mime.c","compat.c") | ForEach-Object { Join-Path $src $_ }
    & clang-cl /nologo /O2 /MD /LD ("/I" + (Join-Path $here "lua")) `
        "/DMIME_API=__declspec(dllexport)" `
        $mimeSrcs /Fo"$objDir\" /Fe"$outMime" /link "$luaObj\lua54.lib" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "luasocket (mime) build failed" }
    foreach ($d in (Split-Path $outSocket), (Split-Path $outMime)) {
        Remove-Item (Join-Path $d "core.lib"), (Join-Path $d "core.exp") -ErrorAction SilentlyContinue
    }
    "luasocket: ok"
}

foreach ($lib in $Libs) {
    switch ($lib) {
        "chimerahash"   { Build-ChimeraHash }
        "sdl2"      { Build-SDL2 }
        "lua54"     { Build-Lua54 }
        "zstd"      { Build-Zstd }
        "cimgui"    { Build-CImGui }
        "openal"    { Build-OpenAL }
        "sqlite3"   { Build-Sqlite3 }
        "chdcapi"   { Build-ChdCapi }
        "luasocket" { Build-LuaSocket }
        default     { throw "unknown lib '$lib'" }
    }
}
