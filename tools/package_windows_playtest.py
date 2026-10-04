#!/usr/bin/env python3
"""Package the complete Windows runtime, not a hand-maintained DLL whitelist."""

import argparse
import hashlib
from pathlib import Path
import zipfile


REQUIRED = (
    "BasisSocial.exe",
    "UnityPlayer.dll",
    "UnityCrashHandler64.exe",
    "dstorage.dll",
    "dstoragecore.dll",
    "WinPixEventRuntime.dll",
    "Start-Basis-Social.cmd",
    "MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll",
)


def inventory(source):
    source = Path(source).resolve(strict=True)
    for name in REQUIRED:
        if not (source / name).is_file():
            raise ValueError(f"Missing required runtime file: {name}")
    if not (source / "BasisSocial_Data").is_dir():
        raise ValueError("Missing BasisSocial_Data directory")

    files = {}
    names = set()
    for path in sorted(source.rglob("*")):
        relative = path.relative_to(source)
        if any(
            part == ".DS_Store"
            or part.endswith("_BackUpThisFolder_ButDontShipItWithYourGame")
            or part.endswith("_BurstDebugInformation_DoNotShip")
            for part in relative.parts
        ):
            continue
        # Keep test instructions and build provenance outside the distributable.
        if relative.as_posix().lower() in {"playtest-ru.md", "build-result.json"}:
            continue
        if path.is_symlink():
            raise ValueError(f"Unexpected symlink in Windows build: {relative}")
        if not path.is_file():
            continue
        name = relative.as_posix()
        if name.casefold() in names:
            raise ValueError(f"Windows case-insensitive filename collision: {name}")
        names.add(name.casefold())
        files[name] = path
    return files


def digest(stream):
    value = hashlib.sha256()
    for block in iter(lambda: stream.read(1024 * 1024), b""):
        value.update(block)
    return value.hexdigest()


def verify(source, archive):
    files = inventory(source)
    with zipfile.ZipFile(archive) as packed:
        entries = packed.namelist()
        if len(entries) != len(set(entries)) or set(entries) != set(files):
            raise ValueError("ZIP inventory does not match the complete runtime")
        bad = packed.testzip()
        if bad is not None:
            raise ValueError(f"ZIP CRC failure: {bad}")
        for name, path in files.items():
            with path.open("rb") as original, packed.open(name) as archived:
                if digest(original) != digest(archived):
                    raise ValueError(f"ZIP file differs from the build: {name}")
    return len(files)


def package(source, archive):
    source = Path(source).resolve(strict=True)
    archive = Path(archive).resolve()
    if archive == source or source in archive.parents:
        raise ValueError("The archive must be outside the build directory")
    files = inventory(source)
    # Exclusive creation preserves previously delivered archives.
    with zipfile.ZipFile(archive, "x", zipfile.ZIP_DEFLATED, compresslevel=6) as packed:
        for name, path in files.items():
            packed.write(path, name)
    return verify(source, archive)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("archive", type=Path)
    args = parser.parse_args()
    count = package(args.source, args.archive)
    with args.archive.open("rb") as stream:
        checksum = digest(stream)
    print(f"Verified {count} files, {args.archive.stat().st_size} bytes")
    print(f"SHA256 {checksum}  {args.archive.name}")


if __name__ == "__main__":
    main()
