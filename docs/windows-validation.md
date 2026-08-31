# Windows validation

Most Social client work can be developed and verified on macOS because the integration
is isolated in `com.basis.social`, its API contract is covered by EditMode tests, and the
same managed assemblies are compiled into each desktop player.

Use Windows at milestones rather than for every edit:

1. After an API/session milestone, run the Social EditMode tests on macOS.
2. After a UI/integration milestone, build and smoke-test the Universal macOS player.
3. Before merging or releasing, produce `StandaloneWindows64` and run the short checklist
   below on a Windows machine or in GitHub Actions.

## Existing GitHub Actions build

`.github/workflows/compilation.yml` already includes a Windows 2022 / `StandaloneWindows64`
matrix entry. It runs only when the repository has its own `UNITY_LICENSE`, `UNITY_EMAIL`,
and `UNITY_PASSWORD` Actions secrets. GitHub forks do not inherit upstream secrets.

No workflow fork is needed. Configure those secrets in this repository if unattended
Windows artifacts are worth the Unity licence maintenance; otherwise use the occasional
Windows laptop.

## One-command laptop build

Install Unity `6000.5.10f1` through Unity Hub on Windows, including Windows player build
support, then run from PowerShell at the repository root:

```powershell
.\tools\build-windows-local.ps1
```

Optional overrides:

```powershell
$env:BASIS_UNITY_EDITOR = "D:\Unity\6000.5.10f1\Editor\Unity.exe"
$env:BASIS_WINDOWS_BUILD_DIR = "D:\Builds\BasisVR"
.\tools\build-windows-local.ps1
```

The player is written to `build\StandaloneWindows64\BasisVR.exe` by default.

## Release-gate smoke checklist

- Launch `BasisVR.exe` without Steam or a VR headset attached.
- Confirm the main menu opens and the Social tab uses the current Basis theme.
- Open Social and verify endpoint, login, password, and sign-in controls render correctly.
- With the local/staging service available, sign in, refresh the profile, and sign out.
- Confirm no access token, refresh token, or password appears in `Player.log`.
- Close the player normally and confirm the process exits without a crash dialog.

The Windows pass is a platform gate, not the primary development loop. Platform-specific
native integrations should stay outside `Basis.Social`; the current package contains no
Windows-only binary or conditional implementation.
