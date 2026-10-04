from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import package_windows_playtest as packaging


class WindowsPackagingTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="basis-package-test-")
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name).resolve()
        self.source = self.root / "Windows"
        self.source.mkdir()
        for name in packaging.REQUIRED + ("BasisSocial_Data/data",):
            self.fixture(name)
        self.archive = self.root / "client.zip"

    def fixture(self, name, content=b"runtime"):
        path = self.source / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)

    def test_all_runtime_files_including_future_dlls_are_included(self):
        self.fixture("NewUnityDependency.dll", b"future library")
        packaging.package(self.source, self.archive)
        with zipfile.ZipFile(self.archive) as packed:
            for name in packaging.REQUIRED:
                self.assertIn(name, packed.namelist())
            self.assertEqual(packed.read("NewUnityDependency.dll"), b"future library")

    def test_instructions_reports_and_debug_backups_are_excluded(self):
        for name in (
            "PLAYTEST-RU.md", "build-result.json", ".DS_Store",
            "BasisSocial_BackUpThisFolder_ButDontShipItWithYourGame/debug.bin",
            "BasisSocial_BurstDebugInformation_DoNotShip/debug.bin",
        ):
            self.fixture(name)
        count = packaging.package(self.source, self.archive)
        self.assertEqual(count, len(packaging.REQUIRED) + 1)

    def test_missing_directstorage_fails_before_creating_archive(self):
        (self.source / "dstorage.dll").unlink()
        with self.assertRaisesRegex(ValueError, "dstorage.dll"):
            packaging.package(self.source, self.archive)
        self.assertFalse(self.archive.exists())

    def test_existing_archive_is_not_overwritten(self):
        self.archive.write_bytes(b"original")
        with self.assertRaises(FileExistsError):
            packaging.package(self.source, self.archive)
        self.assertEqual(self.archive.read_bytes(), b"original")

    def test_cannot_write_archive_inside_build(self):
        with self.assertRaisesRegex(ValueError, "outside"):
            packaging.package(self.source, self.source / "bad.zip")

    def test_windows_case_collision_is_rejected(self):
        # Simulate a case-sensitive source even on case-insensitive macOS disks.
        paths = list(self.source.rglob("*"))
        paths.append(self.source / "DSTORAGE.DLL")
        with patch.object(Path, "rglob", return_value=paths):
            with self.assertRaisesRegex(ValueError, "collision"):
                packaging.package(self.source, self.archive)

    def test_verification_detects_omitted_runtime(self):
        with zipfile.ZipFile(self.archive, "x") as packed:
            packed.write(self.source / "BasisSocial.exe", "BasisSocial.exe")
        with self.assertRaisesRegex(ValueError, "inventory"):
            packaging.verify(self.source, self.archive)

    def test_verification_detects_changed_bytes(self):
        packaging.package(self.source, self.archive)
        self.fixture("UnityPlayer.dll", b"different runtime")
        with self.assertRaisesRegex(ValueError, "differs"):
            packaging.verify(self.source, self.archive)


if __name__ == "__main__":
    unittest.main()
