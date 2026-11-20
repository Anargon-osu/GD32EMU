#!/usr/bin/env python3
"""Package gd32emu and execute the archive's binary in a clean directory."""
import argparse
import base64
import gzip
import io
import json
import urllib.request
import hashlib
import os
from pathlib import Path
import shutil
import struct
import subprocess
import re
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parent.parent


def download(url, maximum):
    with urllib.request.urlopen(url, timeout=120) as response:
        if not response.geturl().startswith("https://"):
            raise ValueError("Runtime package requests must remain on HTTPS")
        content = response.read(maximum + 1)
    if len(content) > maximum:
        raise ValueError("Runtime metadata/package exceeds its size limit")
    return content


def get_json(url):
    content = download(url, 2 * 1024 * 1024)
    if content[:2] == b"\x1f\x8b":
        content = gzip.decompress(content)
    return json.loads(content)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable", type=Path)
    parser.add_argument("rid", choices=("win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"))
    parser.add_argument("--runtime-version", help="exact bundled .NET runtime version; omit for the Windows Framework build")
    args = parser.parse_args()
    output = ROOT / "packages"
    output.mkdir(exist_ok=True)
    name = "gd32emu-" + args.rid
    with tempfile.TemporaryDirectory(prefix="gd32emu package-") as temp:
        temp = Path(temp)
        stage = temp / name
        stage.mkdir()
        binary = stage / ("gd32emu.exe" if args.rid.startswith("win-") else "gd32emu")
        shutil.copy2(args.executable.resolve(), binary)
        binary.chmod(0o755)
        for filename in ("README.md", "LICENSE", "AGENTS.md", "CLAUDE.md", "CODEX.md"):
            shutil.copy2(ROOT / filename, stage / filename)
        shutil.copytree(ROOT / "DOCS", stage / "DOCS")
        shutil.copytree(ROOT / "history", stage / "history")
        if args.runtime_version:
            if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", args.runtime_version):
                parser.error("--runtime-version must be an exact numeric version")
            nuget = Path(os.environ.get("NUGET_PACKAGES", str(Path.home() / ".nuget/packages")))
            notices = stage / "licenses"
            notices.mkdir()
            for kind in ("runtime", "host"):
                pack = nuget / ("microsoft.netcore.app." + kind + "." + args.rid) / args.runtime_version
                filenames = ("LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT")
                if not all((pack / notice).is_file() for notice in filenames):
                    # SDK-installed host packs may lack cache notices; retrieve the exact
                    # official NuGet package and verify its published SHA-512 before reading them.
                    package_name = "microsoft.netcore.app." + kind + "." + args.rid
                    base = "https://api.nuget.org/v3/"
                    entry = get_json(base + "registration5-gz-semver2/" + package_name + "/" + args.runtime_version + ".json")["catalogEntry"]
                    catalog = get_json(entry) if isinstance(entry, str) else entry
                    if catalog.get("packageHashAlgorithm") != "SHA512":
                        raise ValueError("Unexpected NuGet checksum algorithm")
                    url = base + "flatcontainer/" + package_name + "/" + args.runtime_version + "/" + package_name + "." + args.runtime_version + ".nupkg"
                    content = download(url, 512 * 1024 * 1024)
                    if base64.b64encode(hashlib.sha512(content).digest()).decode("ascii") != catalog["packageHash"]:
                        raise ValueError("NuGet package checksum mismatch: " + package_name)
                    with zipfile.ZipFile(io.BytesIO(content)) as runtime_package:
                        for notice in filenames:
                            (notices / ("dotnet-" + kind + "-" + notice)).write_bytes(runtime_package.read(notice))
                else:
                    for notice in filenames:
                        shutil.copy2(pack / notice, notices / ("dotnet-" + kind + "-" + notice))
        (stage / "SHA256SUMS.txt").write_text("".join(
            hashlib.sha256(path.read_bytes()).hexdigest() + "  " + path.relative_to(stage).as_posix() + "\n"
            for path in sorted(stage.rglob("*")) if path.is_file()), encoding="utf-8")
        archive = output / (name + ".zip")
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as package:
            for path in sorted(stage.rglob("*")):
                if path.is_file():
                    package.write(path, name + "/" + path.relative_to(stage).as_posix())
        with zipfile.ZipFile(archive) as package:
            package.extractall(temp / "extracted")
        extracted = temp / "extracted" / name
        executable = extracted / binary.name
        executable.chmod(0o755)
        # SP, reset vector, MOVS r0,#1; ADDS r0,#2; B .
        image = struct.pack("<II", 0x20002000, 0x08000009) + bytes.fromhex("01200230fee7")
        (extracted / "synthetic.bin").write_bytes(image)
        env = os.environ.copy()
        env["DOTNET_ROOT"] = str(temp / "no-runtime")
        env["DOTNET_MULTILEVEL_LOOKUP"] = "0"
        for arguments in (["--help"], ["--list-devices"], ["synthetic.bin", "--steps", "3"]):
            subprocess.run([str(executable)] + arguments, cwd=extracted, env=env, check=True,
                           stdin=subprocess.DEVNULL, timeout=60)
    (output / (archive.name + ".sha256")).write_text(
        hashlib.sha256(archive.read_bytes()).hexdigest() + "  " + archive.name + "\n", encoding="ascii")
    print("Packaged and smoke-tested " + str(archive))


if __name__ == "__main__":
    main()
