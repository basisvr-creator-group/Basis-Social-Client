# macOS development baseline

The project must be opened and built with the exact Unity version recorded in
`Basis/ProjectSettings/ProjectVersion.txt`. The current `developer` baseline uses
Unity `6000.5.10f1`.

Install the Apple Silicon editor and **Mac Build Support (IL2CPP)** in Unity Hub.
The Standalone scripting backend is already IL2CPP in the committed project
settings.

## Local build

From the repository root:

```bash
./tools/build-macos-local.sh
```

The script produces a Universal (`arm64` + `x86_64`) application at
`build/StandaloneOSX/BasisVR.app`. It also re-signs the completed application
after the Basis networking postprocessor changes `Info.plist`. Without this final
step, Unity's earlier signature is invalidated by the plist change.

Useful overrides:

```bash
BASIS_UNITY_EDITOR=/custom/path/to/Unity \
BASIS_MACOS_BUILD_DIR=/custom/build/directory \
./tools/build-macos-local.sh
```

`BASIS_MACOS_CODESIGN_IDENTITY` defaults to `-` (ad-hoc signing) for local
development. A release pipeline must provide its real signing identity and then
perform the normal notarization workflow.

## Verified baseline

On the initial clean macOS build:

- Unity import and C# compilation completed without compiler errors.
- IL2CPP produced a Universal application and all packaged native plugins had
  both `arm64` and `x86_64` slices.
- The application reached the desktop menu and initialized Metal, networking,
  Steam Audio, OpenLipSync and the server browser.
- The current upstream content emits missing-component warnings for the excluded
  OpenVR assembly on macOS and cannot resolve every CJK fallback font.
- Quitting on macOS 27.0 produced a late native crash during process teardown,
  after the managed networking and device cleanup completed. This needs a
  separate native-plugin lifecycle investigation before calling shutdown clean.

The warnings above are baseline observations, not part of the social API layer.
