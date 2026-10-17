"""Verticals desktop launcher for Windows: PostgreSQL, the API, the MCP server and the UI with the agent chat, on loopback.

    python desktop/launcher_windows.py          # run from the repository (first run sets up state and builds the UI)
    python desktop/launcher_windows.py mcp      # print how to connect an agent to the MCP server

A sibling of desktop/launcher.py, which stays the macOS launcher and is not changed for this one. What a running app is
made of comes from there unchanged: its state folder and ports, the gateway that serves the UI, proxies /api with the
token and answers the chat's /__chat/* routes, and the chat itself (desktop/chat). Only what differs on Windows lives here:
where PostgreSQL and Python are, how the database starts, how services start and stop, and the console window's close.
There is no window of its own: open the printed address in a browser.

Same defaults as on macOS: state in .local-verticals-desktop/ (VERTICALS_DESKTOP_STATE moves it), ports 8288, 8299, 8281
and 55539 (VERTICALS_DESKTOP_<UI|API|MCP|PG>_PORT).
"""
from __future__ import annotations

import os
import subprocess
import sys

# Every file the app reads is UTF-8, and so is everything the agents say; Windows would read them in its ANSI code page.
# The chat runs inside this process, so the mode has to be on from the start: run again with it.
if __name__ == "__main__" and not sys.flags.utf8_mode:
    child = subprocess.Popen([sys.executable, "-X", "utf8", *sys.argv])
    while True:
        try:
            sys.exit(child.wait())
        except KeyboardInterrupt:
            pass  # the console's Ctrl-C reaches the child too; it stops everything, then this ends with it

import json  # noqa: E402
import shutil  # noqa: E402
import socket  # noqa: E402
import threading  # noqa: E402
import time  # noqa: E402
from http.server import ThreadingHTTPServer  # noqa: E402
from pathlib import Path  # noqa: E402

sys.path.insert(0, str(Path(__file__).resolve().parent))
import launcher as app  # noqa: E402  (the macOS launcher: its gateway, settings and the chat it imports)

STATE, PGDATA, APP, DIST = app.STATE, app.PGDATA, app.APP, app.DIST
UI_PORT, API_PORT, MCP_PORT, PG_PORT = app.UI_PORT, app.API_PORT, app.MCP_PORT, app.PG_PORT
# Other names this machine's browser reaches the UI by, such as verticals.localhost forwarded from port 80. The gateway
# holds the token and answers only the hosts it knows (DNS rebinding), so each one is named here, never a wildcard.
for _host in filter(None, (h.strip() for h in os.environ.get("VERTICALS_DESKTOP_HOSTS", "").split(","))):
    app.UI_HOSTS.add(_host)
    app.UI_ORIGINS.add(f"http://{_host}")
# The repository's own environment (tools/local.py setup) when there is one, else one of the app's own.
REPO_PYTHON = APP / ".venv" / "Scripts" / "python.exe"
VENV = STATE / "venv"
NPM = shutil.which("npm") or "npm"  # npm.cmd: a bare "npm" is not found without a shell
log = app.log


def run(args, **kw):
    return subprocess.run([str(a) for a in args], check=True, cwd=kw.pop("cwd", APP), **kw)


# ---------------------------------------------------------------- prerequisites

def pg_bin():
    candidates = [os.environ.get("VERTICALS_PG_BIN", "")]
    # The EDB installer's default, then the EDB binaries zip unpacked without admin rights.
    for var, sub in (("ProgramFiles", "PostgreSQL/16/bin"), ("LOCALAPPDATA", "Programs/PostgreSQL/16/bin")):
        if os.environ.get(var):
            candidates.append(str(Path(os.environ[var]) / sub))
    if shutil.which("pg_ctl"):
        candidates.append(str(Path(shutil.which("pg_ctl")).parent))
    for c in candidates:
        ctl = Path(c) / "pg_ctl.exe"
        if c and ctl.is_file() and " 16." in subprocess.check_output([str(ctl), "--version"], text=True):
            return Path(c)
    sys.exit("PostgreSQL 16 is required: install it, unpack its binaries into "
             r"%LOCALAPPDATA%\Programs\PostgreSQL\16, or set VERTICALS_PG_BIN.")


def python():
    if REPO_PYTHON.exists():
        return REPO_PYTHON
    interpreter = VENV / "Scripts" / "python.exe"
    if interpreter.exists():
        return interpreter
    base = [sys.executable] if sys.version_info[:2] == (3, 12) else ["py", "-3.12"]
    log("creating the Python environment")
    # Dependencies only: verticals itself runs from the source tree, as on macOS.
    run([*base, "-m", "venv", VENV])
    deps = json.loads(subprocess.check_output([*base, "-c",
        "import tomllib, json; print(json.dumps(tomllib.load(open('pyproject.toml', 'rb'))['project']['dependencies']))"],
        cwd=APP, text=True))
    run([interpreter, "-m", "pip", "install", "--quiet", *deps])
    return interpreter


def port_free(port):
    with socket.socket() as s:
        # No SO_REUSEADDR: on Windows it lets a bind take a port another process holds.
        try:
            s.bind(("127.0.0.1", port))
        except OSError:
            sys.exit(f"Port {port} is busy. Stop whatever uses it; nothing was killed.")


def build_ui():
    """The UI made with an empty token, as on macOS: the gateway adds the bearer header on its way to the API."""
    if (DIST / "index.html").exists():
        return
    if not (APP / "web" / "node_modules").exists():
        sys.exit("web/node_modules is missing: run  python tools/local.py setup  first.")
    log("building the UI into desktop/build/web")
    env = {k: v for k, v in os.environ.items() if not k.startswith("VITE_")}
    env.update(VITE_VERTICALS_TOKEN="", VITE_ENABLE_DEV_TUNING="0")
    run([NPM, "--prefix", "web", "run", "build", "--", "--outDir", DIST, "--emptyOutDir"], env=env)


# ---------------------------------------------------------------- database

def pg_running(pg):
    return subprocess.run([pg / "pg_ctl", "status", "-D", PGDATA], capture_output=True).returncode == 0


def start_database(pg, cfg, env, py):
    if pg_running(pg):
        # Left running by a launcher that was killed before it could stop it: this app's own database, reused.
        log("reusing the database an earlier start left running")
    else:
        port_free(PG_PORT)
        if not PGDATA.exists():
            log("initialising the database")
            pwfile = STATE / "pg-password"
            try:
                with open(pwfile, "x", opener=lambda p, f: os.open(p, f, 0o600)) as f:
                    f.write(cfg["password"])
                run([pg / "initdb", "-D", PGDATA, "-U", "verticals", "--auth=scram-sha-256",
                     "--pwfile", pwfile, "--encoding=UTF8", "--no-locale"], stdout=subprocess.DEVNULL)
            finally:
                pwfile.unlink(missing_ok=True)
        # No "-k ''": Windows builds have no Unix socket directory, and '' does not survive its command line.
        # A console of its own, never shown, keeps this console's Ctrl-C and close away from PostgreSQL.
        run([pg / "pg_ctl", "-D", PGDATA, "-l", STATE / "postgres.log", "-w", "start", "-o",
             f"-h 127.0.0.1 -p {PG_PORT} -c shared_buffers=32MB -c max_connections=30"],
            stdout=subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW)
    run([py, "-c", """import os, psycopg
from psycopg.conninfo import make_conninfo
with psycopg.connect(make_conninfo(os.environ['VERTICALS_DATABASE_URL'], dbname='postgres'), autocommit=True) as c:
    if not c.execute("SELECT 1 FROM pg_database WHERE datname='verticals'").fetchone():
        c.execute('CREATE DATABASE verticals')
"""], env=env)
    run([py, "-m", "verticals.db.runner", "up"], env=env, stdout=subprocess.DEVNULL)
    if not (STATE / "seeded").exists():
        log("loading the sample board")
        run([py, "-c", """import os, pathlib, psycopg
with psycopg.connect(os.environ['VERTICALS_DATABASE_URL']) as c:
    if not c.execute('SELECT 1 FROM goals LIMIT 1').fetchone():
        c.execute(pathlib.Path('verticals/db/seed_sample.sql').read_text(encoding='utf-8'))
"""], env=env)
        (STATE / "seeded").touch()


# ---------------------------------------------------------------- services

def spawn(name, argv, env):
    """A service in a process group of its own, so the console's Ctrl-C reaches this launcher only and it stops the rest."""
    logfile = open(STATE / f"{name}.log", "ab")
    return subprocess.Popen([str(a) for a in argv], cwd=APP, env=env, stdout=logfile, stderr=subprocess.STDOUT,
                            creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)


def terminate(proc):
    # No process groups to signal on Windows: end the whole tree by its root.
    subprocess.run(["taskkill", "/T", "/F", "/PID", str(proc.pid)], capture_output=True)
    proc.wait()


def on_console_close(cleanup):
    """Closing the console window ends this process without a KeyboardInterrupt, so `finally` never runs. Run the same
    cleanup from the console's close (and logoff, shutdown) event instead; Windows allows it 5 seconds."""
    import ctypes
    from ctypes import wintypes

    def handler(event):
        if event in (2, 5, 6):  # CTRL_CLOSE_EVENT, CTRL_LOGOFF_EVENT, CTRL_SHUTDOWN_EVENT
            cleanup()
            return True
        return False  # Ctrl-C and Ctrl-Break go on to Python's own handler: KeyboardInterrupt
    routine = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.DWORD)(handler)
    ctypes.windll.kernel32.SetConsoleCtrlHandler(routine, True)
    return routine  # the caller keeps it alive; a collected callback would crash the process


def main():
    if os.name != "nt":
        sys.exit("This is the Windows launcher; on macOS run desktop/launcher.py.")
    cfg = app.settings()
    if sys.argv[1:] == ["mcp"]:
        app.write_mcp_config(cfg["token"])
        print(app.mcp_instructions())
        return
    pg = pg_bin()
    for port in (UI_PORT, API_PORT, MCP_PORT):
        port_free(port)
    py = python()
    build_ui()
    env = app.environment(cfg)
    env["PYTHONUTF8"] = "1"  # the services read UTF-8 files too
    procs, state = [], {"db": False, "gateway": None, "chat": None}
    stopping = threading.Lock()

    def shutdown():
        if not stopping.acquire(blocking=False):
            return  # already stopping, from the other of `finally` and the console's close
        if state["gateway"]:
            state["gateway"].shutdown()
        if state["chat"]:
            state["chat"].shutdown()
        for p in reversed(procs):
            if p.poll() is None:
                terminate(p)
        if state["db"]:
            run([pg / "pg_ctl", "-D", PGDATA, "-m", "fast", "-w", "stop"], stdout=subprocess.DEVNULL)

    close_handler = on_console_close(shutdown)  # noqa: F841 (kept alive on purpose)
    try:
        start_database(pg, cfg, env, py)
        state["db"] = True
        procs.append(spawn("api", [py, "-m", "verticals.api.app"], env))
        # The MCP server is told where the app keeps the sessions of tasks (its link tool tells the app).
        procs.append(spawn("mcp", [py, "-m", "verticals.mcp.server", "--transport", "http",
                                   "--gateway-url", f"http://127.0.0.1:{UI_PORT}"], env))
        state["chat"] = app.Chat(STATE, f"http://127.0.0.1:{MCP_PORT}/mcp", cfg["token"],
                                 api_url=f"http://127.0.0.1:{API_PORT}")
        gateway = ThreadingHTTPServer(("127.0.0.1", UI_PORT), app.make_gateway(cfg["token"], state["chat"]))
        gateway.daemon_threads = True
        state["gateway"] = gateway
        threading.Thread(target=gateway.serve_forever, daemon=True).start()
        app.wait_healthy()
        app.write_mcp_config(cfg["token"])
        print(f"\nVerticals desktop is up\n  UI:  http://127.0.0.1:{UI_PORT}/\n  {app.mcp_instructions()}\n"
              f"Ctrl-C stops everything; data stays in {STATE}.\n", flush=True)
        while all(p.poll() is None for p in procs):
            time.sleep(1)
        raise RuntimeError(f"a service exited; see {STATE}\\api.log and {STATE}\\mcp.log")
    except KeyboardInterrupt:
        log("stopping")
    finally:
        shutdown()


if __name__ == "__main__":
    main()
