cmake_minimum_required(VERSION 3.21)

project(DropInOverlay LANGUAGES C CXX)

if(NOT WIN32)
    message(FATAL_ERROR "DropInOverlay is a Windows DLL; cross-compile it with the MinGW-w64 toolchain.")
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# The vendored Universal-Dear-ImGui-Hook is built as a static library and
# linked into this bridge. Disable its example DLL: we expose our own C ABI
# instead of the example's proxy entry points.
set(UIH_BUILD_EXAMPLE OFF CACHE BOOL "" FORCE)
set(UIH_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(UIH_STATIC_RUNTIME ON CACHE BOOL "" FORCE)

add_subdirectory(
    "${CMAKE_CURRENT_SOURCE_DIR}/../../../vendor/Universal-Dear-ImGui-Hook"
    "${CMAKE_CURRENT_BINARY_DIR}/uih"
    EXCLUDE_FROM_ALL)

add_library(dropin_overlay SHARED bridge.cpp)
target_link_libraries(dropin_overlay PRIVATE uih::uih)
set_target_properties(dropin_overlay PROPERTIES
    PREFIX ""
    OUTPUT_NAME "DropInOverlay")

if(MINGW)
    # No libgcc/libstdc++ DLLs next to the game: the bridge must be
    # self-contained like the rest of the package.
    target_link_options(dropin_overlay PRIVATE -static -static-libgcc -static-libstdc++)
endif()

if(NOT MSVC)
    target_compile_options(dropin_overlay PRIVATE -Wall -Wextra)
    target_link_options(dropin_overlay PRIVATE $<$<NOT:$<CONFIG:Debug>>:-s>)
endif()
