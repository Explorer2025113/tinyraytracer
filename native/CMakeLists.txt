cmake_minimum_required(VERSION 3.20)
project(RaytracerNative LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(RaytracerNative SHARED
    src/plugin_api.cpp
    src/pinhole_tracer.cpp
    src/mesh_bvh.cpp
)

target_include_directories(RaytracerNative PUBLIC
    ${CMAKE_CURRENT_SOURCE_DIR}/include
)

target_compile_definitions(RaytracerNative PRIVATE RAYTRACER_NATIVE_BUILD)

find_package(OpenMP)
if(OpenMP_CXX_FOUND)
    target_link_libraries(RaytracerNative PRIVATE OpenMP::OpenMP_CXX)
    target_compile_definitions(RaytracerNative PRIVATE RT_USE_OPENMP=1)
endif()

if(MSVC)
    target_compile_options(RaytracerNative PRIVATE /W4 /EHsc)
endif()
