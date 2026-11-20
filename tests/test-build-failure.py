#!/usr/bin/env python3
"""An invalid test source must make the Windows build command fail."""
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parent.parent


def main():
    if os.name != "nt":
        print("Windows compiler-failure regression: skipped on this OS")
        return 0
    with tempfile.TemporaryDirectory(prefix="gd32emu build failure ") as temp:
        temp = Path(temp)
        (temp / "tests").mkdir()
        for filename in ("build.cmd", "gd32emu.cs", "gd32emu_cli.cs"):
            shutil.copy2(ROOT / filename, temp / filename)
        (temp / "tests" / "EmulatorTests.cs").write_text("intentional invalid C# source\n", encoding="ascii")
        result = subprocess.run([os.environ.get("COMSPEC", "cmd.exe"), "/d", "/c", "build.cmd"], cwd=temp,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                                encoding="utf-8", errors="replace", timeout=60)
        logs = ROOT / "tests" / "work"
        logs.mkdir(exist_ok=True)
        (logs / "compiler-failure.txt").write_text(result.stdout, encoding="utf-8")
        if not (temp / "gd32emu.exe").is_file() or result.returncode == 0 or "error CS" not in result.stdout:
            print(result.stdout)
            print("FAIL: a broken test compiler step was not reported correctly")
            return 1
        print("ok: successful application build followed by invalid test source exits " + str(result.returncode))
        return 0


if __name__ == "__main__":
    sys.exit(main())
