#!/usr/bin/env python3
"""Build a self-contained Verticals for Windows (x64) and its installer: the Windows counterpart of desktop/macos/bundle.py.

    python desktop/windows/bundle.py              # -> desktop/dist/Verticals/, Verticals-Setup.exe and Verticals-windows.zip
    python desktop/windows/bundle.py --out DIR    # build elsewhere (e.g. while an older build runs)
    python desktop/windows/bundle.py --version 0.4   # app version (CI passes the release's)
    python desktop/windows/bundle.py --dev        # a dev build: the yellow icon, so it never passes for the app people use
    python desktop/windows/bundle.py --no-installer  # the folder and the zip only

The app's folder mirrors the repository, as Verticals.app's Resources do: verticals/, desktop/ (launcher, chat, UI build)
and the skills the chat reads (SKILLS below), beside Verticals.exe and its WebView2 libraries, plus Python 3.12 with the
dependencies in site/ and PostgreSQL 16 with only the files it needs. The target PC needs nothing installed (only an agent
CLI for the chat, and WebView2, which the installer fetches where Windows 10 lacks it).

Build machine needs: the .NET SDK (`dotnet`, or DOTNET naming it), uv with a managed Python 3.12 (`uv python install
3.12`), PostgreSQL 16 for Windows (found as the launcher finds it, or VERTICALS_PG_BIN), Inno Setup 6 (`iscc`, or ISCC
naming it) and the UI built into desktop/build/web (see desktop/README.md).
"""
from __future__ import annotations

import hashlib
import os
import shutil
import socket
import struct
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent   # desktop/
REPO = ROOT.parent
HERE = ROOT / "windows"
DIST = Path(sys.argv[sys.argv.index("--out") + 1]).resolve() if "--out" in sys.argv else ROOT / "dist"
APP = DIST / "Verticals"
VERSION = sys.argv[sys.argv.index("--version") + 1] if "--version" in sys.argv else "0.2"
DEV = "--dev" in sys.argv
ICON = HERE / "icon" / ("AppIcon-dev.ico" if DEV else "AppIcon.ico")   # icon/make_icon.py
# The same skills as desktop/macos/bundle.py: desktop/chat/chat.py reads them from the folder it runs in.
SKILLS = ["verticals-operator", "verticals-morning-report"]
PG_KEEP_BINS = ["postgres", "initdb", "pg_ctl"]
PG_KEEP_MODULES = ["plpgsql", "dict_snowball", "pg_trgm"]
# What rarely changes between releases (desktop/macos/bundle.py keeps the same list for its lite update).
RUNTIME_DIRS = ["pg", "python", "site"]
PY_DROP = ["Lib/idlelib", "Lib/tkinter", "Lib/turtledemo", "Lib/ensurepip", "Lib/test", "Lib/site-packages",
           "DLLs/_tkinter.pyd", "DLLs/tcl86t.dll", "DLLs/tk86t.dll", "tcl", "include", "libs", "Scripts", "BUILD"]
# The Visual C++ runtime may be missing on a fresh Windows; it may ship beside the program that needs it.
VC_RUNTIME = {"vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll"}


def sh(*args, **kw):
    done = subprocess.run([str(a) for a in args], text=True, capture_output=True, **kw)
    if done.returncode:
        sys.exit(f"{Path(str(args[0])).name} failed ({done.returncode}):\n{done.stdout[-3000:]}{done.stderr[-3000:]}")
    return done.stdout


def log(msg):
    print(f"[bundle] {msg}", flush=True)


def tool(name, variable, *fallbacks):
    found = os.environ.get(variable) or shutil.which(name) or next((f for f in fallbacks if Path(f).is_file()), None)
    if not found:
        sys.exit(f"{name} is required (or set {variable}); see this file's header")
    return found


# ---------------------------------------------------------------- what a Windows binary links against

def pe_imports(path):
    """The DLL names a PE file imports, directly or delayed: what `otool -L` is to desktop/macos/bundle.py."""
    data = Path(path).read_bytes()
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    sections, optional = struct.unpack_from("<H", data, pe + 6)[0], struct.unpack_from("<H", data, pe + 20)[0]
    opt = pe + 24
    directories = opt + (112 if struct.unpack_from("<H", data, opt)[0] == 0x20B else 96)
    table = opt + optional

    def offset(rva):
        for i in range(sections):
            size, address, _, raw = struct.unpack_from("<IIII", data, table + 40 * i + 8)
            if address <= rva < address + max(size, 1):
                return raw + rva - address
        return None

    def name_at(rva):
        at = offset(rva)
        return data[at:data.index(b"\0", at)].decode("ascii") if at is not None else None

    names = []
    for index, size, name_field in ((1, 20, 12), (13, 32, 4)):  # import table, delay-load table
        rva = struct.unpack_from("<I", data, directories + 8 * index)[0]
        at = offset(rva) if rva else None
        while at is not None and at + size <= len(data):
            entry = data[at:at + size]
            if not any(entry):
                break
            name = name_at(struct.unpack_from("<I", entry, name_field)[0])
            if name:
                names.append(name)
            at += size
    return names


def copy_with_dlls(files, source_dir, dest_dir):
    """Copy the DLLs the given binaries need from source_dir (and the Visual C++ runtime from wherever it is) into
    dest_dir, following each DLL's own imports. System DLLs stay out."""
    copied, queue = {}, list(files)
    system = Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32"
    available = {p.name.lower(): p for p in source_dir.glob("*.dll")}
    while queue:
        for dep in pe_imports(queue.pop()):
            key = dep.lower()
            if key in copied:
                continue
            src = available.get(key)
            if src is None and key in VC_RUNTIME:
                src = next((d / dep for d in (APP / "python", system) if (d / dep).is_file()), None)
            if src is None:
                continue  # Windows' own (kernel32, the C runtime's api-ms-win-crt-*) or an executable such as postgres.exe
            shutil.copy2(src, dest_dir / src.name)
            copied[key] = src
            queue.append(dest_dir / src.name)
    return copied


# ---------------------------------------------------------------- pieces

def build_exe():
    log("Verticals.exe")
    dotnet = tool("dotnet", "DOTNET")
    sh(dotnet, "build", HERE / "Verticals.csproj", "-c", "Release", "-o", APP, "--nologo", "-v", "quiet",
       f"-p:Version={VERSION}", f"-p:VerticalsIcon={ICON.relative_to(HERE)}",
       env={**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1"})
    for leftover in APP.glob("*.pdb"):
        leftover.unlink()


def copy_code():
    log("code")
    skip = shutil.ignore_patterns("__pycache__", "workspace")
    shutil.copytree(REPO / "verticals", APP / "verticals", ignore=skip)
    (APP / "desktop").mkdir()
    shutil.copy2(ROOT / "launcher.py", APP / "desktop/launcher.py")
    shutil.copytree(ROOT / "chat", APP / "desktop/chat", ignore=skip)
    shutil.copytree(ROOT / "build/web", APP / "desktop/build/web")
    for skill in SKILLS:
        shutil.copytree(REPO / ".agents/skills" / skill, APP / ".agents/skills" / skill)


def copy_python():
    log("python")
    uv = tool("uv", "UV")
    interpreter = sh(uv, "python", "find", "--managed-python", "3.12").strip()
    home = Path(interpreter).resolve().parent   # uv's standalone CPython: relocatable, python.exe at its top
    shutil.copytree(home, APP / "python", ignore=shutil.ignore_patterns("__pycache__", "EXTERNALLY-MANAGED"))
    for rel in PY_DROP:
        p = APP / "python" / rel
        shutil.rmtree(p) if p.is_dir() else p.unlink(missing_ok=True)
    (APP / "python/Lib/site-packages").mkdir()
    # site/ is a site directory, not only a path: pywin32 (which mcp needs on Windows) puts its own folders and DLLs on
    # the path through a .pth file there, and Python reads .pth files only in site directories.
    (APP / "python/Lib/site-packages/verticals-site.pth").write_text(
        "import os, site, sys; site.addsitedir(os.path.join(os.path.dirname(sys.prefix), 'site'))\n")
    python = APP / "python/python.exe"
    log("python dependencies")
    # Dependencies only; verticals itself runs from the app's verticals/ (with its SQL files).
    sh(uv, "pip", "install", "--quiet", "--python", python, "--target", APP / "site", "-r", REPO / "pyproject.toml")
    for scripts in ("bin", "Scripts"):
        shutil.rmtree(APP / "site" / scripts, ignore_errors=True)
    for record in (APP / "site").glob("*.dist-info/RECORD"):
        # As on macOS: sorted and without the removed scripts, an unchanged runtime builds to the same bytes.
        lines = [line for line in record.read_text(encoding="utf-8").splitlines() if not line.startswith(("bin/", "../../Scripts", "Scripts/"))]
        record.write_text("\n".join(sorted(lines)) + "\n", encoding="utf-8")
    log("precompiling")
    subprocess.run([python, "-m", "compileall", "-q", "-f", "-j0", "--invalidation-mode", "unchecked-hash",
                    "-s", APP, "-p", "Verticals", APP / "python/Lib", APP / "site", APP / "verticals", APP / "desktop"],
                   check=False)


def pg_home():
    bins = [os.environ.get("VERTICALS_PG_BIN", ""),
            *(str(Path(os.environ[v]) / s) for v, s in (("ProgramFiles", "PostgreSQL/16/bin"), ("LOCALAPPDATA", "Programs/PostgreSQL/16/bin"))
              if os.environ.get(v))]
    for b in filter(None, bins):
        ctl = Path(b) / "pg_ctl.exe"
        if ctl.is_file() and " 16." in sh(ctl, "--version"):
            return Path(b).parent
    sys.exit("PostgreSQL 16 for Windows is required (the EDB binaries zip unpacked), or set VERTICALS_PG_BIN")


def copy_postgres():
    log("postgresql")
    home, pg = pg_home(), APP / "pg"
    bindir, pkglib = pg / "bin", pg / "lib"
    for d in (bindir, pkglib):
        d.mkdir(parents=True)
    # PostgreSQL finds share/ and lib/ beside its bin/; the translations of its messages stay out, as on macOS.
    shutil.copytree(home / "share", pg / "share", ignore=shutil.ignore_patterns("locale", "doc"))
    for f in (pg / "share/extension").iterdir():
        if not f.name.startswith(("plpgsql", "pg_trgm")):
            f.unlink()
    files = []
    for b in PG_KEEP_BINS:
        shutil.copy2(home / "bin" / f"{b}.exe", bindir)
        files.append(bindir / f"{b}.exe")
    for m in PG_KEEP_MODULES:
        shutil.copy2(home / "lib" / f"{m}.dll", pkglib)
        files.append(pkglib / f"{m}.dll")
    copied = copy_with_dlls(files, home / "bin", bindir)
    (pg / "BINDIR").write_text("bin\n")   # the app reads where the binaries are from here, as on macOS
    log(f"postgresql libraries: {', '.join(sorted(p.name for p in copied.values()))}")
    return bindir


def check_postgres(bindir):
    """initdb + start + pg_trgm on a scratch cluster, run from inside the app with a bare PATH."""
    log("checking bundled postgresql")
    tmp = Path(tempfile.mkdtemp())
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        port = s.getsockname()[1]
    system = Path(os.environ.get("SystemRoot", r"C:\Windows"))
    bare = {k: v for k, v in os.environ.items() if k.upper() in ("SYSTEMROOT", "WINDIR", "TEMP", "TMP", "USERPROFILE")}
    bare["PATH"] = f"{system / 'System32'};{system}"
    try:
        sh(bindir / "initdb.exe", "-D", tmp / "data", "-U", "check", "--auth=trust", "--no-locale", "--encoding=UTF8", env=bare)
        # Output to nowhere: the server would hold a captured pipe open after pg_ctl ends (the launcher does the same).
        subprocess.run([str(bindir / "pg_ctl.exe"), "-D", str(tmp / "data"), "-w", "-o", f"-h 127.0.0.1 -p {port}",
                        "-l", str(tmp / "log"), "start"], env=bare, check=True, stdout=subprocess.DEVNULL,
                       stderr=subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            code = ("import psycopg, sys\n"
                    "c = psycopg.connect(host='127.0.0.1', port=sys.argv[1], user='check', dbname='postgres', autocommit=True)\n"
                    "c.execute('CREATE EXTENSION pg_trgm')\n"
                    "print(c.execute(\"SELECT similarity('verticals', 'vertical')\").fetchone()[0])")
            result = sh(APP / "python/python.exe", "-c", code, port,
                        env={**bare, "PYTHONPATH": str(APP / "site"), "PYTHONNOUSERSITE": "1"}).strip()
            log(f"postgresql ok (pg_trgm similarity = {result})")
            # The services the launcher starts, imported the way it starts them (PYTHONPATH as the app sets it); the API
            # wants its settings at import, and nothing connects.
            sh(APP / "python/python.exe", "-c", "import verticals.api.app, verticals.mcp.server",
               env={**bare, "PYTHONPATH": f"{APP};{APP / 'site'}", "PYTHONNOUSERSITE": "1", "PYTHONUTF8": "1",
                    "VERTICALS_DATABASE_URL": f"postgresql://check@127.0.0.1:{port}/postgres", "VERTICALS_TOKEN": "check"},
               cwd=APP)
            log("api and mcp import")
        finally:
            subprocess.run([str(bindir / "pg_ctl.exe"), "-D", str(tmp / "data"), "-m", "fast", "-w", "stop"], env=bare,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def runtime_fingerprint():
    """One hash over every file of the runtime dirs: equal means byte-identical (desktop/macos/bundle.py)."""
    total = hashlib.sha256()
    for top in RUNTIME_DIRS:
        for path in sorted((APP / top).rglob("*")):
            if path.is_file():
                total.update(f"F {path.relative_to(APP).as_posix()} {hashlib.sha256(path.read_bytes()).hexdigest()}\n".encode())
    return total.hexdigest()[:16]


def make_zip():
    """The whole app in one zip, for an update to download (the macOS app's Verticals.zip)."""
    log("zip")
    archive = DIST / "Verticals-windows.zip"
    archive.unlink(missing_ok=True)
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for path in sorted(APP.rglob("*")):
            if path.is_file():
                z.write(path, Path("Verticals") / path.relative_to(APP))
    return archive


def make_installer():
    log("installer")
    iscc = tool("iscc", "ISCC", r"C:\Program Files (x86)\Inno Setup 6\ISCC.exe", r"C:\Program Files\Inno Setup 6\ISCC.exe")
    sh(iscc, "/Q", f"/DAppVersion={VERSION}", f"/DSourceDir={APP}", f"/DOutputDir={DIST}", f"/DIconFile={ICON}",
       HERE / "installer.iss")
    return DIST / "Verticals-Setup.exe"


def size(path):
    total = path.stat().st_size if path.is_file() else sum(p.stat().st_size for p in path.rglob("*") if p.is_file())
    return f"{total / 2**20:.0f} MB"


def main():
    if not (ROOT / "build/web/index.html").exists():
        sys.exit("desktop/build/web is missing; build the UI first (see desktop/README.md)")
    DIST.mkdir(parents=True, exist_ok=True)
    shutil.rmtree(APP, ignore_errors=True)
    APP.mkdir()
    build_exe()
    copy_code()
    copy_python()
    bindir = copy_postgres()
    (APP / "RUNTIME").write_text(runtime_fingerprint() + "\n")
    check_postgres(bindir)
    made = [f"{APP} {VERSION} ({size(APP)})", f"{(z := make_zip())} ({size(z)})"]
    if "--no-installer" not in sys.argv:
        made.append(f"{(setup := make_installer())} ({size(setup)})")
    log(f"done: {', '.join(made)}, runtime {(APP / 'RUNTIME').read_text().strip()}")


if __name__ == "__main__":
    main()
