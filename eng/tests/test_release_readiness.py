from __future__ import annotations

import hashlib
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from eng import release_readiness


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "eng" / "release_readiness.py"


class ReleaseReadinessTests(unittest.TestCase):
    def test_authoritative_header_contains_frozen_export_floor(self) -> None:
        header_exports = release_readiness.parse_header_exports(
            release_readiness.HEADER.read_text(encoding="utf-8")
        )
        frozen_exports = release_readiness.parse_frozen_exports(
            release_readiness.FROZEN_EXPORTS.read_text(encoding="utf-8")
        )

        self.assertTrue(frozen_exports)
        self.assertTrue(frozen_exports <= header_exports)
        self.assertIn("neo_webview_app_retain", header_exports)
        self.assertIn("neo_webview_stream_release", header_exports)

    def test_version_is_read_from_public_version_header(self) -> None:
        version = release_readiness.parse_version(
            release_readiness.VERSION_HEADER.read_text(encoding="utf-8")
        )

        self.assertEqual({"major": 1, "minor": 8}, version)

    def test_checksum_manifest_is_sorted_and_does_not_hash_itself(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            directory = Path(temporary_directory)
            (directory / "nested").mkdir()
            (directory / "z.txt").write_bytes(b"z")
            (directory / "nested" / "a.txt").write_bytes(b"a")

            manifest = release_readiness.write_checksums(directory)
            lines = manifest.read_text(encoding="utf-8").splitlines()

            self.assertEqual(
                [
                    f"{hashlib.sha256(b'a').hexdigest()}  nested/a.txt",
                    f"{hashlib.sha256(b'z').hexdigest()}  z.txt",
                ],
                lines,
            )

    def test_checksum_command_fails_when_artifacts_are_missing(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            result = subprocess.run(
                [sys.executable, str(SCRIPT), "checksums", "--directory", temporary_directory],
                cwd=ROOT,
                text=True,
                capture_output=True,
            )

        self.assertEqual(1, result.returncode)
        self.assertIn("No release-readiness artifacts were found", result.stderr)

    def test_native_command_fails_when_built_binary_is_missing(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            directory = Path(temporary_directory)
            result = subprocess.run(
                [
                    sys.executable,
                    str(SCRIPT),
                    "native",
                    "--rid",
                    "win-x64",
                    "--binary",
                    str(directory / "neowebview_native.dll"),
                    "--runtime-binary",
                    str(directory / "runtime" / "neowebview_native.dll"),
                    "--output",
                    str(directory / "readiness"),
                ],
                cwd=ROOT,
                text=True,
                capture_output=True,
            )

        self.assertEqual(1, result.returncode)
        self.assertIn("Required built native binary was not found", result.stderr)


if __name__ == "__main__":
    unittest.main()
