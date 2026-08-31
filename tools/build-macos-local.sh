#!/usr/bin/env bash

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_dir="$(cd "${script_dir}/.." && pwd)"
project_dir="${repo_dir}/Basis"
project_version_file="${project_dir}/ProjectSettings/ProjectVersion.txt"

if [[ ! -f "${project_version_file}" ]]; then
  echo "Unity project version file not found: ${project_version_file}" >&2
  exit 1
fi

editor_version="$(awk '/^m_EditorVersion:/ { print $2; exit }' "${project_version_file}")"
unity_editor="${BASIS_UNITY_EDITOR:-/Applications/Unity/Hub/Editor/${editor_version}/Unity.app/Contents/MacOS/Unity}"
build_dir="${BASIS_MACOS_BUILD_DIR:-${repo_dir}/build/StandaloneOSX}"
app_path="${build_dir}/BasisVR.app"
build_log="${build_dir}/build.log"
codesign_identity="${BASIS_MACOS_CODESIGN_IDENTITY:--}"

if [[ ! -x "${unity_editor}" ]]; then
  echo "Unity ${editor_version} was not found at: ${unity_editor}" >&2
  echo "Install that exact version or set BASIS_UNITY_EDITOR to its executable." >&2
  exit 1
fi

mkdir -p "${build_dir}"

echo "Building BasisVR for macOS with Unity ${editor_version}"
echo "Output: ${app_path}"

"${unity_editor}" \
  -batchmode \
  -nographics \
  -accept-apiupdate \
  -quit \
  -projectPath "${project_dir}" \
  -buildTarget StandaloneOSX \
  -standaloneBuildSubtarget Player \
  -buildOSXUniversalPlayer "${app_path}" \
  -logFile "${build_log}"

if [[ ! -d "${app_path}" ]]; then
  echo "Unity exited without producing ${app_path}. See ${build_log}." >&2
  exit 1
fi

# BasisCleartextBuildProcess patches Info.plist after Unity's signing stage. Re-sign the
# final bundle so local macOS builds remain launchable. The default '-' identity is ad-hoc;
# release builds can provide a Developer ID via BASIS_MACOS_CODESIGN_IDENTITY.
/usr/bin/codesign --force --deep --sign "${codesign_identity}" "${app_path}"
/usr/bin/codesign --verify --deep --strict --verbose=2 "${app_path}"

echo "macOS build completed: ${app_path}"
echo "Unity log: ${build_log}"
