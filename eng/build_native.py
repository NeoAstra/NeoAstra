#!/usr/bin/env python3
"""Build, test, and stage one NeoWebView native RID."""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
RIDS = {
    "win-x64": ("windows-x64-release", "neowebview_native.dll"),
    "win-arm64": ("windows-arm64-release", "neowebview_native.dll"),
    "osx-x64": ("macos-x64-release", "libneowebview_native.dylib"),
    "osx-arm64": ("macos-arm64-release", "libneowebview_native.dylib"),
    "linux-x64": ("linux-x64-release", "libneowebview_native.so"),
    "linux-arm64": ("linux-arm64-release", "libneowebview_native.so"),
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
        script = Path(temporary_directory) / "neowebview-vsdevcmd.cmd"
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
    runtime_directory = ROOT / "src" / "NeoWebView" / "runtimes" / args.rid / "native"
    if args.clean:
        shutil.rmtree(build_directory, ignore_errors=True)

    run("cmake", "--preset", preset, "-B", str(build_directory))
    run("cmake", "--build", str(build_directory), "--config", "Release")
    if not args.skip_tests:
        run("ctest", "--test-dir", str(build_directory), "--build-config", "Release", "--output-on-failure")

    source = build_directory / library_name
    if not source.is_file():
        raise FileNotFoundError(f"Native build did not produce {source}")
    runtime_directory.mkdir(parents=True, exist_ok=True)
    destination = runtime_directory / library_name
    shutil.copy2(source, destination)
    print(f"Staged {destination.relative_to(ROOT)}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
