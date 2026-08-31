$ErrorActionPreference = "Stop"

$ScriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepositoryDirectory = Split-Path -Parent $ScriptDirectory
$ProjectDirectory = Join-Path $RepositoryDirectory "Basis"
$ProjectVersionFile = Join-Path $ProjectDirectory "ProjectSettings\ProjectVersion.txt"

if (-not (Test-Path -LiteralPath $ProjectVersionFile)) {
    throw "Unity project version file not found: $ProjectVersionFile"
}

$VersionLine = Select-String -LiteralPath $ProjectVersionFile -Pattern '^m_EditorVersion:\s+(.+)$' | Select-Object -First 1
if ($null -eq $VersionLine) {
    throw "Could not read the Unity editor version from: $ProjectVersionFile"
}

$EditorVersion = $VersionLine.Matches[0].Groups[1].Value.Trim()
$DefaultUnityEditor = Join-Path $env:ProgramFiles "Unity\Hub\Editor\$EditorVersion\Editor\Unity.exe"
$UnityEditor = if ($env:BASIS_UNITY_EDITOR) { $env:BASIS_UNITY_EDITOR } else { $DefaultUnityEditor }
$BuildDirectory = if ($env:BASIS_WINDOWS_BUILD_DIR) {
    $env:BASIS_WINDOWS_BUILD_DIR
} else {
    Join-Path $RepositoryDirectory "build\StandaloneWindows64"
}
$PlayerPath = Join-Path $BuildDirectory "BasisVR.exe"
$BuildLog = Join-Path $BuildDirectory "build.log"

if (-not (Test-Path -LiteralPath $UnityEditor -PathType Leaf)) {
    throw "Unity $EditorVersion was not found at: $UnityEditor. Install that exact version or set BASIS_UNITY_EDITOR."
}

New-Item -ItemType Directory -Force -Path $BuildDirectory | Out-Null

Write-Host "Building BasisVR for Windows with Unity $EditorVersion"
Write-Host "Output: $PlayerPath"

$UnityArguments = @(
    "-batchmode",
    "-nographics",
    "-accept-apiupdate",
    "-quit",
    "-projectPath", $ProjectDirectory,
    "-buildTarget", "StandaloneWindows64",
    "-standaloneBuildSubtarget", "Player",
    "-buildWindows64Player", $PlayerPath,
    "-logFile", $BuildLog
)

& $UnityEditor @UnityArguments
if ($LASTEXITCODE -ne 0) {
    throw "Unity exited with code $LASTEXITCODE. See $BuildLog."
}

if (-not (Test-Path -LiteralPath $PlayerPath -PathType Leaf)) {
    throw "Unity exited without producing $PlayerPath. See $BuildLog."
}

Write-Host "Windows build completed: $PlayerPath"
Write-Host "Unity log: $BuildLog"
