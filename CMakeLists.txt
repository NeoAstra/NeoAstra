cmake_minimum_required(VERSION 3.28)

project(NeoAstra VERSION 0.1.0 LANGUAGES C CXX)

option(NEOASTRA_BUILD_SHARED "Build the NeoAstra shared library" ON)
option(NEOASTRA_BUILD_TESTS "Build native tests" OFF)
option(NEOASTRA_BUILD_SAMPLES "Build native samples" OFF)
option(NEOASTRA_ENABLE_ASAN "Enable AddressSanitizer" OFF)
option(NEOASTRA_ENABLE_UBSAN "Enable UndefinedBehaviorSanitizer" OFF)
option(NEOASTRA_ENABLE_TSAN "Enable ThreadSanitizer" OFF)
option(NEOASTRA_ENABLE_CLANG_TIDY "Run Clang static-analyzer checks while compiling" OFF)
option(NEOASTRA_ENABLE_LTO "Enable release link-time optimization" ON)
option(NEOASTRA_WARNINGS_AS_ERRORS "Treat native warnings as errors" OFF)
option(NEOASTRA_RETAIN_RELEASE_SYMBOLS "Generate native release debug symbols for readiness artifacts" OFF)
set(NEOASTRA_WEBVIEW2_SDK_VERSION "1.0.4078.44" CACHE STRING "Pinned WebView2 SDK version")
set(NEOASTRA_LINUX_WEBKIT_API "4.1" CACHE STRING "WebKitGTK API version")

if(MSVC AND NOT CMAKE_CXX_COMPILER_ID MATCHES "Clang")
  message(FATAL_ERROR "NeoAstra requires clang-cl on Windows")
elseif(NOT MSVC AND NOT CMAKE_CXX_COMPILER_ID MATCHES "Clang|AppleClang")
  message(FATAL_ERROR "NeoAstra requires Clang")
endif()

if((NEOASTRA_ENABLE_ASAN OR NEOASTRA_ENABLE_UBSAN OR NEOASTRA_ENABLE_TSAN) AND MSVC)
  message(FATAL_ERROR "NeoAstra sanitizer builds are supported with Clang on Linux and macOS only")
endif()
if(NEOASTRA_ENABLE_CLANG_TIDY AND MSVC)
  message(FATAL_ERROR "NeoAstra Clang static-analyzer builds are supported on Linux and macOS only")
endif()
if(NEOASTRA_ENABLE_TSAN AND (NEOASTRA_ENABLE_ASAN OR NEOASTRA_ENABLE_UBSAN))
  message(FATAL_ERROR "ThreadSanitizer must be enabled in a separate build from AddressSanitizer and UndefinedBehaviorSanitizer")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)

add_library(neoastra_sanitizers INTERFACE)
if(NEOASTRA_ENABLE_ASAN)
  target_compile_options(neoastra_sanitizers INTERFACE -fsanitize=address -fno-omit-frame-pointer)
  target_link_options(neoastra_sanitizers INTERFACE -fsanitize=address)
endif()
if(NEOASTRA_ENABLE_UBSAN)
  # Internal test types cross the hidden-visibility shared-library boundary, so
  # their RTTI is intentionally unavailable to vptr instrumentation.
  target_compile_options(neoastra_sanitizers INTERFACE -fsanitize=undefined -fno-sanitize=vptr -fno-omit-frame-pointer)
  target_link_options(neoastra_sanitizers INTERFACE -fsanitize=undefined -fno-sanitize=vptr)
endif()
if(NEOASTRA_ENABLE_TSAN)
  target_compile_options(neoastra_sanitizers INTERFACE -fsanitize=thread -fno-omit-frame-pointer)
  target_link_options(neoastra_sanitizers INTERFACE -fsanitize=thread)
endif()

set(_neo_library_type STATIC)
if(NEOASTRA_BUILD_SHARED)
  set(_neo_library_type SHARED)
endif()

add_library(neoastra_native ${_neo_library_type}
  native/src/common/neoastra.cpp
)
target_compile_definitions(neoastra_native PRIVATE NEOASTRA_BUILD=1)
target_include_directories(neoastra_native PUBLIC
  $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/native/include>
  $<INSTALL_INTERFACE:include>)
target_link_libraries(neoastra_native PRIVATE neoastra_sanitizers)
set_target_properties(neoastra_native PROPERTIES
  OUTPUT_NAME neoastra_native
  VERSION ${PROJECT_VERSION}
  SOVERSION 1)

if(MSVC)
  target_compile_options(neoastra_native PRIVATE /W4 /permissive- /EHsc /utf-8)
  target_link_options(neoastra_native PRIVATE /guard:cf)
else()
  target_compile_options(neoastra_native PRIVATE -Wall -Wextra -Wpedantic)
endif()

if(NEOASTRA_RETAIN_RELEASE_SYMBOLS)
  if(MSVC)
    target_compile_options(neoastra_native PRIVATE $<$<CONFIG:Release>:/Zi>)
    target_link_options(neoastra_native PRIVATE $<$<CONFIG:Release>:/DEBUG:FULL>)
    set_target_properties(neoastra_native PROPERTIES PDB_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}")
  else()
    target_compile_options(neoastra_native PRIVATE $<$<CONFIG:Release>:-g>)
    target_link_options(neoastra_native PRIVATE $<$<CONFIG:Release>:-g>)
  endif()
endif()

if(NEOASTRA_WARNINGS_AS_ERRORS)
  target_compile_options(neoastra_native PRIVATE $<$<CXX_COMPILER_ID:MSVC,Clang>:/WX> $<$<NOT:$<PLATFORM_ID:Windows>>:-Werror>)
endif()

if(NEOASTRA_ENABLE_CLANG_TIDY)
  find_program(NEOASTRA_CLANG_TIDY_EXECUTABLE NAMES clang-tidy REQUIRED)
  set_property(TARGET neoastra_native PROPERTY CXX_CLANG_TIDY
    "${NEOASTRA_CLANG_TIDY_EXECUTABLE};--checks=-*,clang-analyzer-*;--warnings-as-errors=clang-analyzer-*")
endif()

if(WIN32)
  include(FetchContent)
  FetchContent_Declare(webview2_sdk
    URL "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/${NEOASTRA_WEBVIEW2_SDK_VERSION}/microsoft.web.webview2.${NEOASTRA_WEBVIEW2_SDK_VERSION}.nupkg"
    URL_HASH SHA256=dc4d1d9168df26b830398303e50210b6e1729f6ce5a7ac69d2c766852f489962
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
  FetchContent_MakeAvailable(webview2_sdk)
  if(CMAKE_CXX_COMPILER_TARGET MATCHES "^(ARM64|arm64|aarch64)" OR
     CMAKE_SYSTEM_PROCESSOR MATCHES "^(ARM64|arm64|aarch64)$")
    set(_webview2_arch arm64)
  elseif(CMAKE_SIZEOF_VOID_P EQUAL 8)
    set(_webview2_arch x64)
  else()
    set(_webview2_arch x86)
  endif()
  target_sources(neoastra_native PRIVATE native/src/windows/windows_backend.cpp)
  target_include_directories(neoastra_native PRIVATE "${webview2_sdk_SOURCE_DIR}/build/native/include")
  target_link_libraries(neoastra_native PRIVATE
    user32 ole32 shell32 shcore shlwapi
    "${webview2_sdk_SOURCE_DIR}/build/native/${_webview2_arch}/WebView2LoaderStatic.lib")
elseif(APPLE)
  enable_language(OBJCXX)
  target_sources(neoastra_native PRIVATE native/src/macos/cocoa_backend.mm)
  target_compile_options(neoastra_native PRIVATE -fobjc-arc)
  target_link_libraries(neoastra_native PRIVATE "-framework Cocoa" "-framework Security" "-framework WebKit")
elseif(UNIX)
  find_package(PkgConfig REQUIRED)
  pkg_check_modules(GTK REQUIRED IMPORTED_TARGET gtk+-3.0)
  pkg_check_modules(WEBKIT REQUIRED IMPORTED_TARGET webkit2gtk-${NEOASTRA_LINUX_WEBKIT_API})
  target_sources(neoastra_native PRIVATE native/src/linux/gtk_backend.cpp)
  target_link_libraries(neoastra_native PRIVATE PkgConfig::GTK PkgConfig::WEBKIT)
endif()

include(CheckIPOSupported)
if(NEOASTRA_ENABLE_LTO AND NOT CMAKE_BUILD_TYPE STREQUAL "Debug")
  check_ipo_supported(RESULT _neo_ipo_supported)
  if(_neo_ipo_supported)
    set_property(TARGET neoastra_native PROPERTY INTERPROCEDURAL_OPTIMIZATION TRUE)
  endif()
endif()

if(NEOASTRA_BUILD_TESTS)
  enable_testing()
  add_executable(neoastra_abi_tests native/tests/abi_tests.cpp)
  target_link_libraries(neoastra_abi_tests PRIVATE neoastra_native neoastra_sanitizers)
  add_test(NAME neoastra_abi_tests COMMAND neoastra_abi_tests)
  add_executable(neoastra_abi_1_7_compat_tests native/tests/abi_1_7_compat_tests.cpp)
  add_dependencies(neoastra_abi_1_7_compat_tests neoastra_native)
  add_library(neoastra_abi_compat_sanitizer_suppressions INTERFACE)
  if(NEOASTRA_ENABLE_UBSAN)
    # This fixture deliberately calls dynamic symbols through frozen ABI 1.7
    # structural types, which function-type instrumentation treats as distinct.
    target_compile_options(neoastra_abi_compat_sanitizer_suppressions INTERFACE -fno-sanitize=function)
  endif()
  target_link_libraries(neoastra_abi_1_7_compat_tests PRIVATE
    ${CMAKE_DL_LIBS} neoastra_sanitizers neoastra_abi_compat_sanitizer_suppressions)
  add_test(NAME neoastra_abi_1_7_compat_tests
    COMMAND neoastra_abi_1_7_compat_tests $<TARGET_FILE:neoastra_native>)
  add_executable(neoastra_common_tests native/tests/common_tests.cpp)
  target_include_directories(neoastra_common_tests PRIVATE native/include native/src/common)
  target_link_libraries(neoastra_common_tests PRIVATE neoastra_native neoastra_sanitizers)
  add_test(NAME neoastra_common_tests COMMAND neoastra_common_tests)
  add_executable(neoastra_stress_tests native/tests/stress_tests.cpp)
  target_include_directories(neoastra_stress_tests PRIVATE native/include native/src/common)
  target_link_libraries(neoastra_stress_tests PRIVATE neoastra_native neoastra_sanitizers)
  add_test(NAME neoastra_stress_tests COMMAND neoastra_stress_tests)
endif()

include(GNUInstallDirs)
install(TARGETS neoastra_native EXPORT NeoAstraTargets)
install(FILES native/include/neoastra.h native/include/neoastra_version.h
  DESTINATION ${CMAKE_INSTALL_INCLUDEDIR})
