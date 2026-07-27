#!/usr/bin/env python3
"""Build, test, and stage one NeoAstra native RID."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
RIDS = {
    "win-x64": ("windows-x64-release", "neoastra_native.dll"),
    "win-arm64": ("windows-arm64-release", "neoastra_native.dll"),
    "osx-x64": ("macos-x64-release", "libneoastra_native.dylib"),
    "osx-arm64": ("macos-arm64-release", "libneoastra_native.dylib"),
    "linux-x64": ("linux-x64-release", "libneoastra_native.so"),
    "linux-arm64": ("linux-arm64-release", "libneoastra_native.so"),
}


def run(*arguments: str) -> None:
    print("+", " ".join(arguments), flush=True)
    subprocess.run(arguments, cwd=ROOT, check=True)


def initialize_windows_toolchain(rid: str) -> None:
    if os.name != "nt" or os.environ.get("VCToolsInstallDir"):
        return
    program_files = os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")
    vswhere = Path(program_files) / "Microsoft Visual Studio" / "Installer" / "vswhere.exe"
    if not vswhere.is_file():
        raise FileNotFoundError("Visual Studio vswhere.exe was not found")
    installation = subprocess.check_output(
        [str(vswhere), "-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"],
        text=True,
    ).strip()
    if not installation:
        raise RuntimeError("Visual Studio C++ Build Tools were not found")
    developer_command = Path(installation) / "Common7" / "Tools" / "VsDevCmd.bat"
    architecture = "arm64" if rid == "win-arm64" else "x64"
    with tempfile.TemporaryDirectory() as temporary_directory:
        script = Path(temporary_directory) / "neoastra-vsdevcmd.cmd"
        script.write_text(
            f'@call "{developer_command}" -no_logo -host_arch=x64 -arch={architecture} >nul\n@set\n',
            encoding="utf-8",
        )
        environment = subprocess.check_output(["cmd.exe", "/d", "/c", str(script)], text=True)
    for line in environment.splitlines():
        if "=" in line:
            key, value = line.split("=", 1)
            os.environ[key] = value


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", required=True, choices=sorted(RIDS))
    parser.add_argument("--skip-tests", action="store_true", help="Skip tests for a cross-compiled target")
    parser.add_argument("--clean", action="store_true", help="Remove the RID build directory before configuring")
    args = parser.parse_args()

    preset, library_name = RIDS[args.rid]
    initialize_windows_toolchain(args.rid)
    build_directory = ROOT / "artifacts" / "native" / args.rid
    runtime_directory = ROOT / "src" / "NeoAstra" / "runtimes" / args.rid / "native"
    if args.clean:
        shutil.rmtree(build_directory, ignore_errors=True)

    run("cmake", "--preset", preset, "-B", str(build_directory))
    run("cmake", "--build", str(build_directory), "--config", "Release")
    if not args.skip_tests:
        run("ctest", "--test-dir", str(build_directory), "--build-config", "Release", "--output-on-failure", "--no-tests=error")

    source = build_directory / library_name
    if not source.is_file():
        raise FileNotFoundError(f"Native build did not produce {source}")
    destination = runtime_directory / library_name
    run(
        sys.executable,
        "eng/release_readiness.py",
        "native",
        "--rid",
        args.rid,
        "--binary",
        str(source),
        "--runtime-binary",
        str(destination),
        "--output",
        str(build_directory / "release-readiness"),
    )
    if not destination.is_file():
        raise FileNotFoundError(f"Readiness assembly did not stage {destination}")
    version_header = (ROOT / "native" / "include" / "neoastra_version.h").read_text(encoding="utf-8")
    major = re.search(r"^#define NEOASTRA_ABI_VERSION_MAJOR\s+(\d+)\s*$", version_header, re.MULTILINE)
    minor = re.search(r"^#define NEOASTRA_ABI_VERSION_MINOR\s+(\d+)\s*$", version_header, re.MULTILINE)
    if major is None or minor is None:
        raise ValueError("Native ABI version was not found in neoastra_version.h")
    identity = {
        "schemaVersion": 1,
        "rid": args.rid,
        "file": library_name,
        "sha256": hashlib.sha256(destination.read_bytes()).hexdigest(),
        "abiMajor": int(major.group(1)),
        "abiMinor": int(minor.group(1)),
    }
    identity_path = runtime_directory / "neoastra-native.json"
    identity_path.write_text(json.dumps(identity, separators=(",", ":")) + "\n", encoding="utf-8")
    print(f"Staged {destination.relative_to(ROOT)}", flush=True)
    print(f"Staged {identity_path.relative_to(ROOT)}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
