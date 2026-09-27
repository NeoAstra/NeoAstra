from __future__ import annotations

import unittest
from pathlib import Path

from eng.release_readiness import SUPPORTED_RIDS


ROOT = Path(__file__).resolve().parents[2]


class CiNativePairingTests(unittest.TestCase):
    def test_release_build_consumes_current_run_native_assets_before_msbuild(self) -> None:
        workflow = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        self.assertIn("  native:\n    uses: ./.github/workflows/native.yml", workflow)
        release = workflow.split("\n  release:\n", 1)[1]
        self.assertIn("    needs: [managed, native]", release)
        preparation = release.split("uses: xoofx/.github/.github/actions/dotnet-releaser-action@main", 1)[0]
        # All packaged RIDs, not just the Windows test host, must come from this run.
        # Downloading after MSBuild evaluation is too late for its native item globs.
        for rid in SUPPORTED_RIDS:
            with self.subTest(rid=rid):
                self.assertIn(
                    "uses: actions/download-artifact@v4\n"
                    "        with:\n"
                    f"          name: native-{rid}\n"
                    f"          path: src/NeoAstra.Core/runtimes/{rid}/native",
                    preparation,
                )
        self.assertNotIn("run-id:", preparation)
        self.assertNotIn("continue-on-error:", preparation)
