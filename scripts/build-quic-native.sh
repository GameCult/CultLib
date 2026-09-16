#!/usr/bin/env bash
# Builds the CultMesh native QUIC bridge for Linux x64.
#
# The Windows counterpart (build-quic-native.ps1) takes MsQuic from a pinned
# NuGet package. There is no Linux binary in any MsQuic NuGet and the GitHub
# releases for v2.5.9 carry only test bundles, so the runtime here comes from
# Microsoft's Debian 13 pool and the headers come from the msquic source tree at
# the matching tag. Both are pinned by SHA-256 the same way the NuGet is: a
# digest that stops matching is a changed dependency, not a warning.
#
# This must run on a Debian 13 host (the container in .github/workflows, or the
# workstation's Docker engine for the development loop). The artifact is linked
# against that distribution's glibc, so where it was built is part of what it is.
#
#   scripts/build-quic-native.sh [output-directory]
#
# Default output: artifacts/quic-native/linux-x64, which is git-ignored build
# output and never delivery.

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="2.5.9"
output_directory="${1:-${repo_root}/artifacts/quic-native/linux-x64}"
case "${output_directory}" in
  /*) ;;
  *) output_directory="${repo_root}/${output_directory}" ;;
esac

cache_root="${repo_root}/artifacts/dependencies/msquic-linux-${version}"
deb_path="${cache_root}/libmsquic_${version}_amd64.deb"
extract_root="${cache_root}/package"
include_root="${cache_root}/include"
build_root="${repo_root}/artifacts/quic-native-build/linux-x64/Release"

deb_url="https://packages.microsoft.com/debian/13/prod/pool/main/libm/libmsquic/libmsquic_${version}_amd64.deb"
deb_sha256="1baa61ade0b7b4a99f6dcb6b00d9aedb12b5566d00918a325be7425e878e51ba"
header_base="https://raw.githubusercontent.com/microsoft/msquic/v${version}/src/inc"

# The three headers the POSIX build needs. msquic.h includes msquic_posix.h,
# which includes quic_sal_stub.h; the NuGet ships only the Windows trio.
header_names=("msquic.h" "msquic_posix.h" "quic_sal_stub.h")
header_sha256s=(
  "c9abfdd02c45910649dd335d6bd82718e4ddd2fdb35fe550567c78f032551e0c"
  "b285fa66b9c9bdc886c30ef92910da472692b25f5c6192416fb40f08f64e22ec"
  "9b13328d9aec8807a754b2bc391b31b5d09b1c5f6cec064012051683ed169055"
)

verify_sha256() {
  local path="$1" expected="$2"
  local actual
  actual="$(sha256sum "${path}" | cut -d' ' -f1)"
  if [[ "${actual}" != "${expected}" ]]; then
    echo "Digest mismatch for ${path}" >&2
    echo "  expected ${expected}" >&2
    echo "  actual   ${actual}" >&2
    exit 1
  fi
}

mkdir -p "${cache_root}" "${include_root}"

if [[ ! -f "${deb_path}" ]]; then
  echo "Fetching ${deb_url}"
  curl -fsSL -o "${deb_path}" "${deb_url}"
fi
verify_sha256 "${deb_path}" "${deb_sha256}"

if [[ ! -f "${extract_root}/usr/lib/x86_64-linux-gnu/libmsquic.so.${version}" ]]; then
  rm -rf "${extract_root}"
  mkdir -p "${extract_root}"
  dpkg-deb -x "${deb_path}" "${extract_root}"
fi

for index in "${!header_names[@]}"; do
  header="${header_names[${index}]}"
  target="${include_root}/${header}"
  if [[ ! -f "${target}" ]]; then
    echo "Fetching ${header_base}/${header}"
    curl -fsSL -o "${target}" "${header_base}/${header}"
  fi
  verify_sha256 "${target}" "${header_sha256s[${index}]}"
done

library_directory="${extract_root}/usr/lib/x86_64-linux-gnu"
# CMake links `-lmsquic`, which wants a plain `libmsquic.so` to resolve against;
# the deb ships only the versioned file and its SONAME symlink.
ln -sf "libmsquic.so.${version}" "${library_directory}/libmsquic.so"

cmake -S "${repo_root}/native/GameCult.Mesh.Quic.Native" -B "${build_root}" -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DMSQUIC_INCLUDE_DIR="${include_root}" \
  -DMSQUIC_LIB_DIR="${library_directory}"
cmake --build "${build_root}"

mkdir -p "${output_directory}"
cp "${build_root}/bin/libgamecult_mesh_quic_native.so" "${output_directory}/"
# The real file under the SONAME the bridge records, not the symlink: what ships
# beside the bridge has to be a file, because the package directory is copied.
cp "${library_directory}/libmsquic.so.${version}" "${output_directory}/libmsquic.so.2"
# The deb carries three library files and no licence text, so the MIT licence
# comes from the copy this repo already committed for the Windows plugin. It is
# the same text for both platforms.
cp "${repo_root}/unity/org.gamecult.cultlib/Third Party Notices/MSQUIC-LICENSE.txt" \
  "${output_directory}/MSQUIC-LICENSE.txt"

echo "CultMesh native QUIC runtime: ${output_directory}"
ls -l "${output_directory}"
sha256sum "${output_directory}"/* | sed "s|${output_directory}/||"
