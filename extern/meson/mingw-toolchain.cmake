# CMake toolchain for the cmake-driven native deps (SDL2, openal-soft) when
# cross-building Windows artifacts from Linux. Mirrors extern/meson/mingw-w64.ini.
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_PROCESSOR x86_64)

set(CMAKE_C_COMPILER x86_64-w64-mingw32-gcc)
set(CMAKE_CXX_COMPILER x86_64-w64-mingw32-g++)
set(CMAKE_RC_COMPILER x86_64-w64-mingw32-windres)

set(CMAKE_FIND_ROOT_PATH /usr/x86_64-w64-mingw32)
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)

# static gcc runtime: produced dlls must not depend on libgcc/libwinpthread dlls
set(CMAKE_SHARED_LINKER_FLAGS_INIT "-static")
set(CMAKE_EXE_LINKER_FLAGS_INIT "-static")
