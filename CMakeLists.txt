cmake_minimum_required(VERSION 3.28)

project(NeoWebView VERSION 0.1.0 LANGUAGES C CXX)

option(NEOWEBVIEW_BUILD_SHARED "Build the NeoWebView shared library" ON)
option(NEOWEBVIEW_BUILD_TESTS "Build native tests" OFF)
option(NEOWEBVIEW_BUILD_SAMPLES "Build native samples" OFF)
option(NEOWEBVIEW_ENABLE_ASAN "Enable AddressSanitizer" OFF)
option(NEOWEBVIEW_ENABLE_UBSAN "Enable UndefinedBehaviorSanitizer" OFF)
option(NEOWEBVIEW_ENABLE_TSAN "Enable ThreadSanitizer" OFF)
option(NEOWEBVIEW_ENABLE_LTO "Enable release link-time optimization" ON)
option(NEOWEBVIEW_WARNINGS_AS_ERRORS "Treat native warnings as errors" OFF)
set(NEOWEBVIEW_WEBVIEW2_SDK_VERSION "1.0.4078.44" CACHE STRING "Pinned WebView2 SDK version")
set(NEOWEBVIEW_LINUX_WEBKIT_API "4.1" CACHE STRING "WebKitGTK API version")

if(MSVC AND NOT CMAKE_CXX_COMPILER_ID MATCHES "Clang")
  message(FATAL_ERROR "NeoWebView requires clang-cl on Windows")
elseif(NOT MSVC AND NOT CMAKE_CXX_COMPILER_ID MATCHES "Clang|AppleClang")
  message(FATAL_ERROR "NeoWebView requires Clang")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)

set(_neo_library_type STATIC)
if(NEOWEBVIEW_BUILD_SHARED)
  set(_neo_library_type SHARED)
endif()

add_library(neowebview_native ${_neo_library_type}
  native/src/common/neowebview.cpp
)
target_compile_definitions(neowebview_native PRIVATE NEOWEBVIEW_BUILD=1)
target_include_directories(neowebview_native PUBLIC
  $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/native/include>
  $<INSTALL_INTERFACE:include>)
set_target_properties(neowebview_native PROPERTIES
  OUTPUT_NAME neowebview_native
  VERSION ${PROJECT_VERSION}
  SOVERSION 1)

if(MSVC)
  target_compile_options(neowebview_native PRIVATE /W4 /permissive- /EHsc /utf-8)
  target_link_options(neowebview_native PRIVATE /guard:cf)
else()
  target_compile_options(neowebview_native PRIVATE -Wall -Wextra -Wpedantic)
endif()

if(NEOWEBVIEW_WARNINGS_AS_ERRORS)
  target_compile_options(neowebview_native PRIVATE $<$<CXX_COMPILER_ID:MSVC,Clang>:/WX> $<$<NOT:$<PLATFORM_ID:Windows>>:-Werror>)
endif()

if(NEOWEBVIEW_ENABLE_ASAN)
  target_compile_options(neowebview_native PRIVATE -fsanitize=address)
  target_link_options(neowebview_native PRIVATE -fsanitize=address)
endif()
if(NEOWEBVIEW_ENABLE_UBSAN)
  target_compile_options(neowebview_native PRIVATE -fsanitize=undefined)
  target_link_options(neowebview_native PRIVATE -fsanitize=undefined)
endif()
if(NEOWEBVIEW_ENABLE_TSAN)
  target_compile_options(neowebview_native PRIVATE -fsanitize=thread)
  target_link_options(neowebview_native PRIVATE -fsanitize=thread)
endif()

if(WIN32)
  target_sources(neowebview_native PRIVATE native/src/windows/windows_backend.cpp)
  target_link_libraries(neowebview_native PRIVATE user32 ole32 shell32 shcore)
elseif(APPLE)
  enable_language(OBJCXX)
  target_sources(neowebview_native PRIVATE native/src/macos/cocoa_backend.mm)
  target_compile_options(neowebview_native PRIVATE -fobjc-arc)
  target_link_libraries(neowebview_native PRIVATE "-framework Cocoa" "-framework WebKit")
elseif(UNIX)
  find_package(PkgConfig REQUIRED)
  pkg_check_modules(GTK REQUIRED IMPORTED_TARGET gtk+-3.0)
  pkg_check_modules(WEBKIT REQUIRED IMPORTED_TARGET webkit2gtk-${NEOWEBVIEW_LINUX_WEBKIT_API})
  target_sources(neowebview_native PRIVATE native/src/linux/gtk_backend.cpp)
  target_link_libraries(neowebview_native PRIVATE PkgConfig::GTK PkgConfig::WEBKIT)
endif()

include(CheckIPOSupported)
if(NEOWEBVIEW_ENABLE_LTO AND NOT CMAKE_BUILD_TYPE STREQUAL "Debug")
  check_ipo_supported(RESULT _neo_ipo_supported)
  if(_neo_ipo_supported)
    set_property(TARGET neowebview_native PROPERTY INTERPROCEDURAL_OPTIMIZATION TRUE)
  endif()
endif()

if(NEOWEBVIEW_BUILD_TESTS)
  enable_testing()
  add_executable(neowebview_abi_tests native/tests/abi_tests.cpp)
  target_link_libraries(neowebview_abi_tests PRIVATE neowebview_native)
  add_test(NAME neowebview_abi_tests COMMAND neowebview_abi_tests)
endif()

include(GNUInstallDirs)
install(TARGETS neowebview_native EXPORT NeoWebViewTargets)
install(FILES native/include/neowebview.h native/include/neowebview_version.h
  DESTINATION ${CMAKE_INSTALL_INCLUDEDIR})
