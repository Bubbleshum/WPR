cmake_minimum_required(VERSION 3.16)
project(wprcpu CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
add_library(wprcpu SHARED shim.cpp)
target_include_directories(wprcpu PRIVATE
    $ENV{HOME}/dyn/src
    $ENV{HOME}/dyn/externals/mcl/include
    $ENV{HOME}/dyn/externals/fmt/include)
target_link_directories(wprcpu PRIVATE
    $ENV{HOME}/dyn/build-win/src/dynarmic
    $ENV{HOME}/dyn/build-win/externals/mcl/src
    $ENV{HOME}/dyn/build-win/externals/fmt
    $ENV{HOME}/dyn/build-win/externals/zydis
    $ENV{HOME}/dyn/build-win/externals/zydis/zycore)
target_link_libraries(wprcpu PRIVATE dynarmic mcl fmt Zydis Zycore)
target_link_options(wprcpu PRIVATE -static -static-libgcc -static-libstdc++)
set_target_properties(wprcpu PROPERTIES PREFIX "" OUTPUT_NAME "wprcpu")
