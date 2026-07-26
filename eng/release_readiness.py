#!/usr/bin/env python3
"""Assemble and verify NeoWebView release-readiness artifacts."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SUPPORTED_RIDS = {
    "win-x64": "neowebview_native.dll",
    "win-arm64": "neowebview_native.dll",
    "osx-x64": "libneowebview_native.dylib",
    "osx-arm64": "libneowebview_native.dylib",
    "linux-x64": "libneowebview_native.so",
    "linux-arm64": "libneowebview_native.so",
}
HEADER = ROOT / "native" / "include" / "neowebview.h"
VERSION_HEADER = ROOT / "native" / "include" / "neowebview_version.h"
FROZEN_EXPORTS = ROOT / "native" / "tests" / "abi_1_7_exports.inc"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def require_file(path: Path, description: str) -> None:
    if not path.is_file():
        raise FileNotFoundError(f"Required {description} was not found: {path}")


def find_tool(*names: str) -> str:
    for name in names:
        tool = shutil.which(name)
        if tool:
            return tool
    raise FileNotFoundError(f"Required tool was not found on PATH (tried {', '.join(names)})")


def run_capture(arguments: list[str]) -> str:
    print("+", " ".join(arguments), flush=True)
    result = subprocess.run(arguments, cwd=ROOT, text=True, capture_output=True)
    if result.returncode:
        details = result.stderr.strip() or result.stdout.strip() or "no diagnostic output"
        raise RuntimeError(f"Command failed with exit code {result.returncode}: {' '.join(arguments)}\n{details}")
    return result.stdout


def run(arguments: list[str]) -> None:
    print("+", " ".join(arguments), flush=True)
    subprocess.run(arguments, cwd=ROOT, check=True)


def parse_header_exports(text: str) -> set[str]:
    exports = set(
        re.findall(
            r"NEO_WEBVIEW_API\s+[^;#]*?\b(neo_webview_[a-z0-9_]+)\s*\(",
            text,
            flags=re.IGNORECASE,
        )
    )
    lifetime_types = re.findall(r"NEO_WEBVIEW_DECLARE_LIFETIME\(([a-z0-9_]+)\)\s*;", text)
    for lifetime_type in lifetime_types:
        exports.add(f"neo_webview_{lifetime_type}_retain")
        exports.add(f"neo_webview_{lifetime_type}_release")
    return exports


def parse_frozen_exports(text: str) -> set[str]:
    return set(re.findall(r"^\s*NEO_ABI_1_7_EXPORT\((neo_webview_[a-z0-9_]+)\)", text, re.MULTILINE))


def parse_version(text: str) -> dict[str, int]:
    version: dict[str, int] = {}
    for component in ("MAJOR", "MINOR"):
        match = re.search(rf"^#define NEO_WEBVIEW_ABI_VERSION_{component}\s+(\d+)\s*$", text, re.MULTILINE)
        if match is None:
            raise ValueError(f"ABI {component.lower()} version was not found in {VERSION_HEADER}")
        version[component.lower()] = int(match.group(1))
    return version


def inspect_binary_exports(rid: str, binary: Path) -> tuple[set[str], str]:
    if rid.startswith("win-"):
        llvm_readobj = shutil.which("llvm-readobj")
        if llvm_readobj:
            output = run_capture([llvm_readobj, "--coff-exports", str(binary)])
            exports = set(re.findall(r"^\s*Name:\s+(neo_webview_[a-z0-9_]+)\s*$", output, re.MULTILINE))
            tool = "llvm-readobj --coff-exports"
        else:
            dumpbin = find_tool("dumpbin")
            output = run_capture([dumpbin, "/nologo", "/exports", str(binary)])
            exports = set(re.findall(r"\b(neo_webview_[a-z0-9_]+)\b", output))
            tool = "dumpbin /exports"
    elif rid.startswith("osx-"):
        nm = find_tool("nm")
        output = run_capture([nm, "-gU", str(binary)])
        exports = {name.removeprefix("_") for name in re.findall(r"\b_?(neo_webview_[a-z0-9_]+)\s*$", output, re.MULTILINE)}
        tool = "nm -gU"
    else:
        nm = find_tool("llvm-nm", "nm")
        output = run_capture([nm, "-D", "--defined-only", str(binary)])
        exports = set(re.findall(r"\b(neo_webview_[a-z0-9_]+)(?:@@?\S+)?\s*$", output, re.MULTILINE))
        tool = f"{Path(nm).name} -D --defined-only"
    if not exports:
        raise RuntimeError(f"Export inspection found no neo_webview_ symbols in {binary} using {tool}")
    return exports, tool


def copy_windows_symbols(binary: Path, symbols_directory: Path) -> None:
    pdb = binary.with_suffix(".pdb")
    require_file(pdb, "Windows linker PDB")
    shutil.copy2(pdb, symbols_directory / pdb.name)


def create_macos_symbols(binary: Path, symbols_directory: Path) -> None:
    dsymutil = find_tool("dsymutil")
    destination = symbols_directory / f"{binary.name}.dSYM"
    run([dsymutil, str(binary), "-o", str(destination)])
    if not any(path.is_file() for path in destination.rglob("*")):
        raise RuntimeError(f"dsymutil did not produce debug symbol files in {destination}")


def create_linux_symbols(binary: Path, symbols_directory: Path) -> Path:
    objcopy = find_tool("llvm-objcopy", "objcopy")
    destination = symbols_directory / f"{binary.name}.debug"
    run([objcopy, "--only-keep-debug", str(binary), str(destination)])
    require_file(destination, "Linux detached debug symbols")
    if destination.stat().st_size == 0:
        raise RuntimeError(f"Linux detached debug symbol file is empty: {destination}")
    return Path(objcopy)


def strip_runtime_binary(rid: str, runtime_binary: Path, linux_objcopy: Path | None) -> None:
    if rid.startswith("osx-"):
        strip = find_tool("strip")
        run([strip, "-S", str(runtime_binary)])
    elif rid.startswith("linux-"):
        if linux_objcopy is None:
            raise RuntimeError("Linux debug symbols were not extracted before staging the runtime binary")
        run([str(linux_objcopy), "--strip-debug", str(runtime_binary)])


def symbol_evidence(symbols_directory: Path, output_directory: Path) -> list[dict[str, str]]:
    files = sorted(path for path in symbols_directory.rglob("*") if path.is_file())
    if not files:
        raise RuntimeError(f"No native debug symbol files were assembled in {symbols_directory}")
    return [
        {"path": path.relative_to(output_directory).as_posix(), "sha256": sha256(path)}
        for path in files
    ]


def write_checksums(directory: Path) -> Path:
    if not directory.is_dir():
        raise NotADirectoryError(f"Readiness artifact directory was not found: {directory}")
    manifest = directory / "SHA256SUMS"
    files = sorted(
        path for path in directory.rglob("*")
        if path.is_file() and path != manifest
    )
    if not files:
        raise FileNotFoundError(f"No release-readiness artifacts were found under {directory}")
    contents = "".join(f"{sha256(path)}  {path.relative_to(directory).as_posix()}\n" for path in files)
    manifest.write_text(contents, encoding="utf-8", newline="\n")
    print(f"Wrote {manifest}", flush=True)
    return manifest


def prepare_native(rid: str, binary: Path, runtime_binary: Path, output_directory: Path) -> None:
    expected_name = SUPPORTED_RIDS[rid]
    if binary.name != expected_name:
        raise ValueError(f"RID {rid} requires native binary name {expected_name}, received {binary.name}")
    require_file(binary, "built native binary")
    require_file(HEADER, "public ABI header")
    require_file(VERSION_HEADER, "public ABI version header")
    require_file(FROZEN_EXPORTS, "frozen ABI export fixture")

    output_directory.parent.mkdir(parents=True, exist_ok=True)
    temporary_directory = Path(tempfile.mkdtemp(prefix=f".{output_directory.name}-", dir=output_directory.parent))
    try:
        symbols_directory = temporary_directory / "symbols" / rid
        symbols_directory.mkdir(parents=True)
        linux_objcopy: Path | None = None
        if rid.startswith("win-"):
            copy_windows_symbols(binary, symbols_directory)
        elif rid.startswith("osx-"):
            create_macos_symbols(binary, symbols_directory)
        else:
            linux_objcopy = create_linux_symbols(binary, symbols_directory)

        assembled_binary = temporary_directory / "runtimes" / rid / "native" / expected_name
        assembled_binary.parent.mkdir(parents=True)
        shutil.copy2(binary, assembled_binary)
        strip_runtime_binary(rid, assembled_binary, linux_objcopy)

        include_directory = temporary_directory / "include"
        include_directory.mkdir()
        assembled_header = include_directory / HEADER.name
        assembled_version_header = include_directory / VERSION_HEADER.name
        shutil.copy2(HEADER, assembled_header)
        shutil.copy2(VERSION_HEADER, assembled_version_header)

        abi_directory = temporary_directory / "abi"
        abi_directory.mkdir()
        assembled_frozen_exports = abi_directory / FROZEN_EXPORTS.name
        shutil.copy2(FROZEN_EXPORTS, assembled_frozen_exports)

        shutil.copy2(ROOT / "THIRD-PARTY-NOTICES.md", temporary_directory / "THIRD-PARTY-NOTICES.md")
        shutil.copy2(ROOT / "license.txt", temporary_directory / "license.txt")
        docs_directory = temporary_directory / "docs"
        docs_directory.mkdir()
        for document in ("platform-support.md", "known-limitations.md"):
            source = ROOT / "doc" / document
            require_file(source, "runtime dependency documentation")
            shutil.copy2(source, docs_directory / document)

        header_exports = parse_header_exports(assembled_header.read_text(encoding="utf-8"))
        frozen_exports = parse_frozen_exports(assembled_frozen_exports.read_text(encoding="utf-8"))
        binary_exports, export_tool = inspect_binary_exports(rid, assembled_binary)
        missing_header_exports = sorted(header_exports - binary_exports)
        unexpected_binary_exports = sorted(binary_exports - header_exports)
        frozen_missing_from_header = sorted(frozen_exports - header_exports)
        frozen_missing_from_binary = sorted(frozen_exports - binary_exports)

        report = {
            "schema_version": 1,
            "rid": rid,
            "abi_version": parse_version(assembled_version_header.read_text(encoding="utf-8")),
            "evidence": {
                "binary": {
                    "path": assembled_binary.relative_to(temporary_directory).as_posix(),
                    "sha256": sha256(assembled_binary),
                    "export_inspection": export_tool,
                },
                "header": {
                    "path": assembled_header.relative_to(temporary_directory).as_posix(),
                    "sha256": sha256(assembled_header),
                },
                "version_header": {
                    "path": assembled_version_header.relative_to(temporary_directory).as_posix(),
                    "sha256": sha256(assembled_version_header),
                },
                "frozen_export_fixture": {
                    "path": assembled_frozen_exports.relative_to(temporary_directory).as_posix(),
                    "sha256": sha256(assembled_frozen_exports),
                },
                "debug_symbols": symbol_evidence(symbols_directory, temporary_directory),
            },
            "exports": {
                "public_header": sorted(header_exports),
                "built_binary": sorted(binary_exports),
                "frozen_abi_1_7_floor": sorted(frozen_exports),
            },
            "validation": {
                "status": "pass" if not any((missing_header_exports, unexpected_binary_exports, frozen_missing_from_header, frozen_missing_from_binary)) else "fail",
                "header_exports_missing_from_binary": missing_header_exports,
                "binary_exports_missing_from_header": unexpected_binary_exports,
                "frozen_exports_missing_from_header": frozen_missing_from_header,
                "frozen_exports_missing_from_binary": frozen_missing_from_binary,
            },
        }
        report_path = abi_directory / "abi-report.json"
        report_path.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8", newline="\n")
        if report["validation"]["status"] != "pass":
            failures = report["validation"]
            raise RuntimeError(
                "ABI export validation failed: "
                f"header missing from binary={failures['header_exports_missing_from_binary']}, "
                f"binary missing from header={failures['binary_exports_missing_from_header']}, "
                f"frozen missing from header={failures['frozen_exports_missing_from_header']}, "
                f"frozen missing from binary={failures['frozen_exports_missing_from_binary']}"
            )

        write_checksums(temporary_directory)
        if output_directory.exists():
            shutil.rmtree(output_directory)
        temporary_directory.replace(output_directory)
        runtime_binary.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(output_directory / "runtimes" / rid / "native" / expected_name, runtime_binary)
        print(f"Assembled {output_directory}", flush=True)
    except BaseException:
        shutil.rmtree(temporary_directory, ignore_errors=True)
        raise


def create_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    native_parser = subparsers.add_parser("native", help="Assemble one RID's native readiness artifacts")
    native_parser.add_argument("--rid", required=True, choices=sorted(SUPPORTED_RIDS))
    native_parser.add_argument("--binary", required=True, type=Path)
    native_parser.add_argument("--runtime-binary", required=True, type=Path)
    native_parser.add_argument("--output", required=True, type=Path)

    checksum_parser = subparsers.add_parser("checksums", help="Write SHA256SUMS for an assembled directory")
    checksum_parser.add_argument("--directory", required=True, type=Path)
    return parser


def main() -> int:
    args = create_parser().parse_args()
    try:
        if args.command == "native":
            prepare_native(args.rid, args.binary.resolve(), args.runtime_binary.resolve(), args.output.resolve())
        else:
            write_checksums(args.directory.resolve())
        return 0
    except (OSError, RuntimeError, ValueError, subprocess.SubprocessError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
