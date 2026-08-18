cmake_minimum_required(VERSION 3.15)
cmake_policy(SET CMP0091 NEW)

project(libjxr2uhdr VERSION 0.1.0 LANGUAGES C CXX)

if(NOT WIN32)
  message(FATAL_ERROR "libjxr2uhdr requires Windows WIC and can only be built on Windows.")
endif()

if(NOT MSVC)
  message(FATAL_ERROR "libjxr2uhdr requires MSVC.")
endif()

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
  message(FATAL_ERROR "libjxr2uhdr supports x64 builds only.")
endif()

include(FetchContent)

set(BUILD_SHARED_LIBS OFF CACHE BOOL "Build dependencies as static libraries" FORCE)
set(UHDR_BUILD_EXAMPLES OFF CACHE BOOL "Build libultrahdr examples" FORCE)
set(UHDR_ENABLE_INSTALL OFF CACHE BOOL "Install libultrahdr" FORCE)

FetchContent_Declare(libultrahdr
  GIT_REPOSITORY https://github.com/google/libultrahdr.git
  GIT_TAG v1.4.0
  GIT_SHALLOW TRUE
)
FetchContent_MakeAvailable(libultrahdr)

add_library(jxr2uhdr SHARED src/jxr2uhdr.c)
target_include_directories(jxr2uhdr
  PUBLIC
    ${CMAKE_CURRENT_LIST_DIR}/include
  PRIVATE
    ${libultrahdr_SOURCE_DIR}
)
target_compile_features(jxr2uhdr PRIVATE c_std_11)

set_property(TARGET jxr2uhdr PROPERTY
  MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>DLL")

target_link_libraries(jxr2uhdr PRIVATE
  uhdr
  ole32
  uuid
  windowscodecs
)

set_target_properties(jxr2uhdr PROPERTIES
  LINKER_LANGUAGE CXX
  OUTPUT_NAME "jxr2uhdr"
  PUBLIC_HEADER "${CMAKE_CURRENT_LIST_DIR}/include/jxr2uhdr.h"
)
