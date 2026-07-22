cmake_minimum_required(VERSION 3.24)
project(gamecult_mesh_quic_native LANGUAGES CXX)

if(NOT WIN32)
  message(FATAL_ERROR "The current CultMesh native QUIC connector supports Windows desktop only")
endif()
if(NOT DEFINED MSQUIC_ROOT)
  message(FATAL_ERROR "MSQUIC_ROOT must point at the extracted Microsoft.Native.Quic.MsQuic.Schannel package build/native directory")
endif()

add_library(gamecult_mesh_quic_native SHARED cultmesh_quic_native.cpp)
target_compile_features(gamecult_mesh_quic_native PRIVATE cxx_std_20)
target_compile_definitions(gamecult_mesh_quic_native PRIVATE GAMECULT_MESH_QUIC_NATIVE_EXPORTS WIN32_LEAN_AND_MEAN NOMINMAX)
target_include_directories(gamecult_mesh_quic_native PRIVATE "${MSQUIC_ROOT}/include")
target_link_directories(gamecult_mesh_quic_native PRIVATE "${MSQUIC_ROOT}/lib/x64")
target_link_libraries(gamecult_mesh_quic_native PRIVATE msquic crypt32 bcrypt)

set_target_properties(gamecult_mesh_quic_native PROPERTIES
  OUTPUT_NAME "gamecult_mesh_quic_native"
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
