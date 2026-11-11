"""Check that the publishing SDK forwards the short-lived NuGet API key."""

import http.server
import os
import shutil
import subprocess
import tempfile
import threading
import tomllib
import unittest
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]


@unittest.skipUnless(shutil.which("dotnet"), "A .NET SDK is required for the NuGet publishing check")
class NuGetPublishingTests(unittest.TestCase):
    def test_publishing_sdk_sends_api_key_from_environment(self) -> None:
        configuration = ROOT / "src" / "dotnet-releaser.toml"
        settings = tomllib.loads(configuration.read_text(encoding="utf-8"))
        artifacts = configuration.parent / settings.get("artifacts_folder", "artifacts-dotnet-releaser")

        # Use the same SDK scope without creating the artifacts folder: dotnet-releaser requires it not to exist yet.
        with tempfile.TemporaryDirectory(dir=artifacts.parent, prefix="publishing-check-") as temporary_directory:
            directory = Path(temporary_directory)
            sdk = subprocess.run(["dotnet", "--version"], cwd=directory, text=True, capture_output=True, timeout=30)
            if sdk.returncode and "A compatible .NET SDK was not found" in sdk.stderr:
                self.skipTest("The SDK selected for NuGet publishing is not installed")
            self.assertEqual(0, sdk.returncode, sdk.stdout + sdk.stderr)

            package = directory / "NeoAstra.LocalProbe.0.0.1.nupkg"
            with zipfile.ZipFile(package, "w") as archive:
                archive.writestr(
                    "NeoAstra.LocalProbe.nuspec",
                    '<package><metadata><id>NeoAstra.LocalProbe</id><version>0.0.1</version>'
                    '<authors>Local probe</authors><description>Loopback only.</description></metadata></package>',
                )

            key = "neoastra-local-probe-not-a-real-api-key"
            received = []

            class Sink(http.server.BaseHTTPRequestHandler):
                def do_PUT(self) -> None:
                    self.rfile.read(int(self.headers.get("Content-Length", "0")))
                    received.append(self.headers.get("X-NuGet-ApiKey"))
                    self.send_response(201)
                    self.end_headers()

                def log_message(self, *args) -> None:
                    pass

            with http.server.HTTPServer(("127.0.0.1", 0), Sink) as server:
                worker = threading.Thread(target=server.serve_forever, daemon=True)
                worker.start()
                try:
                    # Use only a fake key and a loopback sink; this never publishes to a package feed.
                    result = subprocess.run(
                        [
                            "dotnet", "nuget", "push", str(package),
                            "--source", f"http://127.0.0.1:{server.server_port}/api/v2/package",
                            "--no-symbols", "--allow-insecure-connections",
                        ],
                        cwd=directory,
                        env=dict(os.environ, NUGET_API_KEY=key, NUGET_SYMBOL_API_KEY=key),
                        text=True,
                        capture_output=True,
                        timeout=45,
                    )
                finally:
                    server.shutdown()
                    worker.join()

            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertEqual([key], received, f"SDK {sdk.stdout.strip()} did not forward the NuGet API key")


if __name__ == "__main__":
    unittest.main()
