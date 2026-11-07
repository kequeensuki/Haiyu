#!/usr/bin/env python3
"""Sequential, fail-closed validation of the existing Haiyu export test projects.

Python 3.10+, standard library only. Does not install dependencies, edit sources,
commit, or push. A successful native check executes the published binary itself.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

TEST_PROJECT = Path("src/Tests/ABIRuntime.TransportTests/ABIRuntime.TransportTests.csproj")
SMOKE_PROJECT = Path("src/Tests/GameDataExport.ProtocolSmoke/GameDataExport.ProtocolSmoke.csproj")


def find_repo(explicit: str | None) -> Path:
    candidates = [Path(explicit).resolve()] if explicit else list(Path(__file__).resolve().parents)
    for root in candidates:
        if (root / TEST_PROJECT).is_file() and (root / SMOKE_PROJECT).is_file():
            return root
    raise ValueError("Cannot locate Haiyu. Pass --repo pointing to its root.")


def fingerprint(repo: Path) -> str:
    result = subprocess.run(
        ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard", "--"],
        cwd=repo, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )
    extensions = {".cs", ".csproj", ".props", ".targets", ".proto", ".json", ".resx", ".config", ".sln", ".slnx"}
    digest = hashlib.sha256()
    names = {name for name in result.stdout.split(b"\0") if name}
    for raw in sorted(names):
        name = os.fsdecode(raw)
        path = repo / name
        if path.suffix.lower() not in extensions:
            continue
        digest.update(raw + b"\0")
        if not path.is_file():
            digest.update(b"<deleted>")
            continue
        digest.update(path.read_bytes())
        digest.update(b"\0")
    return digest.hexdigest()


def host_rid() -> str:
    system = {"Linux": "linux", "Windows": "win", "Darwin": "osx"}.get(platform.system())
    arch = {"x86_64": "x64", "amd64": "x64", "arm64": "arm64", "aarch64": "arm64"}.get(platform.machine().lower())
    if not system or not arch:
        raise ValueError("Unsupported native host; provide a suitable native build environment.")
    return f"{system}-{arch}"


def project_info(path: Path) -> tuple[str, str]:
    root = ET.parse(path).getroot()
    framework = root.findtext(".//TargetFramework")
    if not framework or ";" in framework:
        raise ValueError(f"Review the project's current target framework manually: {path}")
    return framework, root.findtext(".//AssemblyName") or path.stem


def verify_managed_log(text: str, expected: list[str], minimum: int) -> int:
    if "ALL TESTS PASSED" not in text:
        raise ValueError("Managed test completion marker is missing.")
    count = len(re.findall(r"^PASS\s+", text, re.MULTILINE))
    if count < minimum:
        raise ValueError(f"Only {count} PASS records; required at least {minimum}.")
    for name in expected:
        pattern = r"^PASS\s+" + re.escape(name) + r"\s+\d+\s+ms\s*$"
        if not re.search(pattern, text, re.MULTILINE):
            raise ValueError(f"Requested new test did not PASS (check registration/filter): {name}")
    return count


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo")
    parser.add_argument("--dotnet", help="SDK executable; otherwise resolve dotnet from PATH")
    parser.add_argument("--artifacts-dir", help="Parent directory OUTSIDE the repository; a unique run directory is created")
    parser.add_argument("--native", action="store_true")
    parser.add_argument("--rid", help="Native RID; must match this host")
    parser.add_argument("--no-restore", action="store_true")
    parser.add_argument("--expected-test", action="append", default=[], help="Exact new test display name; repeat for multiple tests")
    parser.add_argument("--min-tests", type=int, default=1)
    args = parser.parse_args(argv)
    if args.min_tests < 1 or (args.rid and not args.native):
        parser.error("--min-tests must be positive; --rid requires --native")
    try:
        repo = find_repo(args.repo)
        sdk = shutil.which(args.dotnet or "dotnet")
        if not sdk:
            raise ValueError("dotnet SDK not found; pass --dotnet. No automatic installation was attempted.")
        sdk = str(Path(sdk).resolve())
        parent = Path(args.artifacts_dir).resolve() if args.artifacts_dir else None
        if parent and parent.is_relative_to(repo):
            raise ValueError("Keep logs and native artifacts outside the source repository.")
        if parent:
            parent.mkdir(parents=True, exist_ok=True)
        run_dir = Path(tempfile.mkdtemp(prefix="haiyu-export-checks-", dir=parent))
        baseline = fingerprint(repo)
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        print(f"FAIL setup: {error}", file=sys.stderr)
        return 2

    summary = {"status": "failed", "repo": str(repo), "artifacts": str(run_dir),
               "inputFingerprint": baseline, "expectedTests": args.expected_test,
               "nativeStatus": "not_run" if args.native else "not_requested", "steps": []}
    env = os.environ.copy()
    env.setdefault("DOTNET_PROCESSOR_COUNT", "2")
    env.setdefault("DOTNET_CLI_HOME", str(run_dir / "dotnet-home"))

    def step(name: str, command: list[str]) -> str:
        log = run_dir / f"{name}.log"
        print(f"RUN {name}; log={log}", flush=True)
        with log.open("wb") as output:
            result = subprocess.run(command, cwd=repo, env=env, stdout=output, stderr=subprocess.STDOUT)
        summary["steps"].append({"name": name, "command": command, "exitCode": result.returncode, "log": str(log)})
        if fingerprint(repo) != baseline:
            raise ValueError("Source inputs changed during verification. Freeze inputs and rerun; do not trust these binaries.")
        if result.returncode:
            raise ValueError(f"{name} exited {result.returncode}; inspect {log}")
        return log.read_text(encoding="utf-8-sig", errors="replace")

    try:
        version = step("sdk", [sdk, "--version"]).strip()
        selected = re.search(r"^(\d+)\.", version, re.MULTILINE)
        if not selected or int(selected.group(1)) < 10:
            raise ValueError("Selected SDK must support the repository's .NET 10 projects.")
        common = ["-c", "Release", "-m:1", "-p:UseSharedCompilation=false"]
        restore = ["--no-restore"] if args.no_restore else []
        step("build", [sdk, "build", str(TEST_PROJECT), *restore, *common])
        framework, assembly = project_info(repo / TEST_PROJECT)
        dll = repo / TEST_PROJECT.parent / "bin" / "Release" / framework / f"{assembly}.dll"
        log = step("managed-tests", [sdk, str(dll), "--game-data-export"])
        summary["managedPassCount"] = verify_managed_log(log, args.expected_test, args.min_tests)
        if args.native:
            summary["nativeStatus"] = "running"
            rid = host_rid()
            if args.rid and args.rid != rid:
                raise ValueError(f"Cross-host native execution is not validated by this script: host={rid}, requested={args.rid}")
            mode = step("native-mode", [sdk, "msbuild", str(SMOKE_PROJECT), "-getProperty:PublishAot",
                                         "-p:Configuration=Release", f"-p:RuntimeIdentifier={rid}"])
            if not re.search(r"^true\s*$", mode, re.MULTILINE | re.IGNORECASE):
                raise ValueError("Smoke project is not configured for PublishAot; inspect its project configuration.")
            native_dir = run_dir / "native"
            step("native-publish", [sdk, "publish", str(SMOKE_PROJECT), *restore, *common,
                                   "-r", rid, "-o", str(native_dir)])
            _, assembly = project_info(repo / SMOKE_PROJECT)
            executable = native_dir / (assembly + (".exe" if rid.startswith("win-") else ""))
            if not executable.is_file():
                raise ValueError(f"Native executable missing: {executable}")
            with executable.open("rb") as binary:
                magic = binary.read(4)
            if magic != b"\x7fELF" and not magic.startswith(b"MZ") and magic not in {
                b"\xcf\xfa\xed\xfe", b"\xfe\xed\xfa\xcf", b"\xca\xfe\xba\xbe", b"\xbe\xba\xfe\xca"
            }:
                raise ValueError("Unexpected native executable format; refusing to run a wrapper as a native test.")
            text = step("native-tests", [str(executable)])
            if "PASS NativeAOT protocol smoke" not in text:
                raise ValueError("Native process exited successfully but did not report smoke assertions passing.")
            summary["nativeStatus"] = "passed"
            summary["nativeRid"] = rid
        summary["status"] = "passed"
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        summary["error"] = str(error)
        if summary["nativeStatus"] == "running":
            summary["nativeStatus"] = "failed"
        print(f"FAIL: {error}", file=sys.stderr, flush=True)
    finally:
        try:
            summary["finalFingerprint"] = fingerprint(repo)
            if summary["finalFingerprint"] != baseline:
                summary["status"] = "failed"
                summary["error"] = "Inputs changed during verification. Rerun with frozen sources."
        except (OSError, subprocess.CalledProcessError) as error:
            summary["status"] = "failed"
            summary["error"] = f"Could not verify final input fingerprint: {error}"
        report = run_dir / "summary.json"
        report.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"{summary['status'].upper()}: {report}", flush=True)
    return 0 if summary["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
