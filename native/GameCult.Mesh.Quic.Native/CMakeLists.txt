cmake_minimum_required(VERSION 3.24)
project(gamecult_mesh_quic_native LANGUAGES CXX)

# Two platforms, one source. Windows takes MsQuic from the pinned OpenSSL NuGet
# layout (MSQUIC_ROOT); Linux takes it from the extracted libmsquic deb, whose
# headers and library land in unrelated places, so they are named separately.
if(NOT WIN32 AND NOT (CMAKE_SYSTEM_NAME STREQUAL "Linux"))
  message(FATAL_ERROR "The CultMesh native QUIC bridge supports Windows x64 and Linux x64")
endif()

add_library(gamecult_mesh_quic_native SHARED cultmesh_quic_native.cpp)
target_compile_features(gamecult_mesh_quic_native PRIVATE cxx_std_20)
# include/cultmesh_quic_native.h is the host-facing contract, not an internal
# detail: the bridge includes it, and so does any consumer that can.
target_include_directories(gamecult_mesh_quic_native PUBLIC include)

if(WIN32)
  if(NOT DEFINED MSQUIC_ROOT)
    message(FATAL_ERROR "MSQUIC_ROOT must point at the extracted Microsoft.Native.Quic.MsQuic.OpenSSL package build/native directory")
  endif()
  # Static CRT. A Node or Unity host without the Visual C++ redistributable
  # installed cannot load a DLL that imports vcruntime140.dll, and the failure
  # arrives as "the specified module could not be found" with nothing naming the
  # missing runtime. The bridge carries its own instead.
  set_property(TARGET gamecult_mesh_quic_native PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded")
  target_compile_definitions(gamecult_mesh_quic_native PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
  target_include_directories(gamecult_mesh_quic_native PRIVATE "${MSQUIC_ROOT}/include")
  target_link_directories(gamecult_mesh_quic_native PRIVATE "${MSQUIC_ROOT}/lib/x64")
  # crypt32 and bcrypt belong to the v1 shim alone: CryptHashCertificate2
  # compares the Unity pin. Nothing in the v2 path needs them. ntdll carries
  # RtlIpv4/6StringToAddressEx, which msquic_winuser.h's inline QuicAddrFromString
  # calls to parse the listener host; the Linux header parses it in-tree instead.
  target_link_libraries(gamecult_mesh_quic_native PRIVATE msquic crypt32 bcrypt ntdll)
  # The DLL is committed beside its source, so a rebuild must reproduce it: /Brepro drops the link timestamps.
  target_compile_options(gamecult_mesh_quic_native PRIVATE /Brepro)
  target_link_options(gamecult_mesh_quic_native PRIVATE /Brepro)
else()
  if(NOT DEFINED MSQUIC_INCLUDE_DIR OR NOT DEFINED MSQUIC_LIB_DIR)
    message(FATAL_ERROR "MSQUIC_INCLUDE_DIR and MSQUIC_LIB_DIR must point at the msquic v2.5.9 headers and the extracted libmsquic library directory")
  endif()
  target_include_directories(gamecult_mesh_quic_native PRIVATE "${MSQUIC_INCLUDE_DIR}")
  target_link_directories(gamecult_mesh_quic_native PRIVATE "${MSQUIC_LIB_DIR}")
  target_link_libraries(gamecult_mesh_quic_native PRIVATE msquic pthread)
  # Only the CULTMESH_API exports leave the .so; everything else stays internal.
  target_compile_options(gamecult_mesh_quic_native PRIVATE -fvisibility=hidden -fvisibility-inlines-hidden)
  # libmsquic.so.2 ships beside the bridge rather than being installed
  # system-wide, so the loader is pointed at the bridge's own directory.
  set_target_properties(gamecult_mesh_quic_native PROPERTIES
    BUILD_WITH_INSTALL_RPATH TRUE
    INSTALL_RPATH "$ORIGIN")
endif()

set_target_properties(gamecult_mesh_quic_native PROPERTIES
  OUTPUT_NAME "gamecult_mesh_quic_native"
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
  LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
