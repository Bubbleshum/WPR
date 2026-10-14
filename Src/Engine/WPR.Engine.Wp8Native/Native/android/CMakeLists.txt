# libwprcpu.so: the dynarmic A32 JIT behind wprcpu's C ABI, for Android.
# arm64-v8a uses dynarmic's oaknut (arm64) backend, x86_64 (the emulator) its xbyak backend.
cmake_minimum_required(VERSION 3.12)
project(wprcpu C CXX ASM)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

set(DYNARMIC_TESTS OFF CACHE BOOL "" FORCE)
set(DYNARMIC_USE_BUNDLED_EXTERNALS ON CACHE BOOL "" FORCE)
set(DYNARMIC_WARNINGS_AS_ERRORS OFF CACHE BOOL "" FORCE)
set(DYNARMIC_USE_PRECOMPILED_HEADERS OFF CACHE BOOL "" FORCE)
set(DYNARMIC_FRONTENDS "A32" CACHE STRING "" FORCE)
add_subdirectory(${DYNARMIC_SOURCE} dyn)

add_library(wprcpu SHARED ${CMAKE_CURRENT_LIST_DIR}/../shim.cpp)
target_link_libraries(wprcpu PRIVATE dynarmic)
target_link_options(wprcpu PRIVATE -Wl,--gc-sections -Wl,-z,max-page-size=16384)
