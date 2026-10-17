"""Docker-free local development. Persistent, isolated PostgreSQL; Ctrl-C stops services.

Usage: python3 tools/local.py setup | start
Requires Python 3.12, Node 22+, and PostgreSQL 16 (including pg_trgm). Runs on macOS, Linux and Windows.
Never reads the repository .env or touches an existing database.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import secrets
import shutil
import signal
import socket
import subprocess
import sys
import threading
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
STATE = ROOT / '.local-verticals'
PGDATA = STATE / 'postgres'
WINDOWS = os.name == 'nt'
EXE = '.exe' if WINDOWS else ''
PYTHON = ROOT / ('.venv/Scripts/python.exe' if WINDOWS else '.venv/bin/python')
# npm is npm.cmd on Windows; a bare 'npm' is not found there without a shell.
NPM = shutil.which('npm') or 'npm'


def run(args, **kwargs):
    return subprocess.run([str(a) for a in args], check=True, cwd=ROOT, **kwargs)


def pg_bin():
    candidates = [os.environ.get('VERTICALS_PG_BIN', ''),
                  '/opt/homebrew/opt/postgresql@16/bin',
                  '/usr/local/opt/postgresql@16/bin', '/usr/lib/postgresql/16/bin']
    if WINDOWS:
        # The EDB installer's default, then the EDB binaries zip unpacked without admin rights.
        for var, sub in (('ProgramFiles', 'PostgreSQL/16/bin'), ('LOCALAPPDATA', 'Programs/PostgreSQL/16/bin')):
            if os.environ.get(var):
                candidates.append(str(Path(os.environ[var]) / sub))
    found = shutil.which('pg_ctl')
    if found:
        candidates.append(str(Path(found).parent))
    for candidate in candidates:
        if candidate and (Path(candidate) / f'pg_ctl{EXE}').is_file():
            version = subprocess.check_output([str(Path(candidate) / f'pg_ctl{EXE}'), '--version'], text=True)
            if ' 16.' in version:
                return Path(candidate)
    raise SystemExit('PostgreSQL 16 required. Set VERTICALS_PG_BIN to its bin directory.')


def config():
    STATE.mkdir(mode=0o700, exist_ok=True)
    path = STATE / 'config.json'
    if not path.exists():
        with open(path, 'x', opener=lambda p, f: os.open(p, f, 0o600)) as stream:
            json.dump({'token': secrets.token_urlsafe(32), 'password': secrets.token_urlsafe(32)}, stream)
    return json.loads(path.read_text(encoding='utf-8'))


def environment(settings):
    env = os.environ.copy()
    # Explicit values prevent dotenv from selecting a user's production/test database.
    env.update(VERTICALS_DATABASE_URL=f"postgresql://verticals:{settings['password']}@127.0.0.1:55439/verticals",
               VERTICALS_TOKEN=settings['token'], VERTICALS_OWNER='local',
               VERTICALS_BIND='127.0.0.1:8099', VERTICALS_POOL_MIN='1', VERTICALS_POOL_MAX='4',
               VITE_VERTICALS_TOKEN=settings['token'], VITE_ENABLE_DEV_TUNING='0',
               VITE_API_PROXY_TARGET='http://127.0.0.1:8099',
               # Source files are UTF-8; Windows would otherwise read them in its ANSI code page.
               PYTHONUTF8='1')
    return env


def port_free(port):
    with socket.socket() as sock:
        if not WINDOWS:  # on Windows SO_REUSEADDR lets a bind take a port another process holds
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            sock.bind(('127.0.0.1', port))
        except OSError:
            raise SystemExit(f'Port {port} is occupied. Stop its owner first; no process was killed.')


def pg_running(pg):
    return subprocess.run([pg / 'pg_ctl', 'status', '-D', PGDATA], capture_output=True).returncode == 0


def database(pg, settings):
    if pg_running(pg):
        # This instance's own database, left running by a start that never got to stop it (a killed launcher): reuse it.
        print('Reusing the local PostgreSQL an earlier start left running.', flush=True)
        return
    port_free(55439)
    if not PGDATA.exists():
        password = STATE / 'pg-password'
        try:
            with open(password, 'x', opener=lambda p, f: os.open(p, f, 0o600)) as stream:
                stream.write(settings['password'])
            run([pg / 'initdb', '-D', PGDATA, '-U', 'verticals', '--auth=scram-sha-256',
                 '--pwfile', password, '--encoding=UTF8', '--no-locale'])
        finally:
            password.unlink(missing_ok=True)
    # Windows builds have no Unix socket directory by default, and '' does not survive its command line.
    socket_dir = '' if WINDOWS else "-k '' "
    # pg_ctl detaches PostgreSQL from the terminal on POSIX. On Windows it would share this console, whose Ctrl-C and
    # close would hit it before the API and can kill it half way: a console of its own, never shown, keeps both away.
    detach = {'creationflags': subprocess.CREATE_NO_WINDOW} if WINDOWS else {}
    try:
        run([pg / 'pg_ctl', '-D', PGDATA, '-l', STATE / 'postgres.log', '-w', 'start',
             '-o', f"-h 127.0.0.1 -p 55439 {socket_dir}-c shared_buffers=32MB -c max_connections=20"], **detach)
    except subprocess.CalledProcessError:
        raise SystemExit(f"PostgreSQL did not start; see {STATE / 'postgres.log'}.")


def venv_python():
    """A Python 3.12 to create .venv with: this one if it is 3.12, else the py launcher on Windows."""
    if sys.version_info[:2] == (3, 12):
        return [sys.executable]
    if WINDOWS and shutil.which('py'):
        return ['py', '-3.12']
    return ['python3.12']


def spawn(command, env):
    """A service in its own process group, so stopping it stops what it started too."""
    if WINDOWS:
        # A new group also keeps the console's Ctrl-C away from it: this script stops it instead.
        return subprocess.Popen(command, cwd=ROOT, env=env, creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
    return subprocess.Popen(command, cwd=ROOT, env=env, start_new_session=True)


def terminate(process):
    if WINDOWS:
        # No process groups to signal: end the whole tree (npm.cmd -> node) by its root.
        subprocess.run(['taskkill', '/T', '/F', '/PID', str(process.pid)], capture_output=True)
        process.wait()
        return
    os.killpg(process.pid, signal.SIGTERM)
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        os.killpg(process.pid, signal.SIGKILL)
        process.wait()


def on_console_close(cleanup):
    """Windows: closing the console window ends this script without a KeyboardInterrupt, so `finally` never runs.
    Run the same cleanup from the console's close (and logoff, shutdown) event instead; Windows allows it 5 seconds."""
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
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['setup', 'start'])
    args = parser.parse_args()
    pg = pg_bin()
    settings = config()
    env = environment(settings)
    if args.command == 'setup':
        lock = json.loads((ROOT / 'web/package-lock.json').read_text(encoding='utf-8'))
        for name in ['konstantinopolskii-design-system-2.1.1.tgz']:
            archive = ROOT / 'web/vendor' / name
            if not archive.is_file():
                raise SystemExit(f'Missing vendored dependency web/vendor/{name}; see README.')
            expected = lock['packages']['node_modules/@konstantinopolskii/design-system']['integrity']
            actual = 'sha512-' + base64.b64encode(hashlib.sha512(archive.read_bytes()).digest()).decode()
            if actual != expected:
                raise SystemExit(f'Integrity mismatch: web/vendor/{name}. Supply the lockfile-matching archive.')
        if not PYTHON.exists():
            run([*venv_python(), '-m', 'venv', ROOT / '.venv'])
        run([PYTHON, '-m', 'pip', 'install', '-e', '.[dev]'])
        run([NPM, '--prefix', 'web', 'ci'])
        run([NPM, '--prefix', 'web', 'run', 'typecheck'])
        run([NPM, '--prefix', 'web', 'run', 'build'], env=env)
        print('Setup complete. Run: python3 tools/local.py start', flush=True)
        return
    if not PYTHON.exists() or not (ROOT / 'web/dist/index.html').exists():
        raise SystemExit('Run python3 tools/local.py setup first.')
    port_free(8099)
    port_free(8088)
    processes = []
    started = False
    stopping = threading.Lock()

    def shutdown():
        if not stopping.acquire(blocking=False):
            return  # already stopping, from the other of `finally` and the console's close
        for process in reversed(processes):
            if process.poll() is None:
                terminate(process)
        if started and pg_running(pg):
            run([pg / 'pg_ctl', '-D', PGDATA, '-m', 'fast', '-w', 'stop'])

    def stop(signum, frame):
        raise KeyboardInterrupt
    signal.signal(signal.SIGTERM, stop)
    close_handler = on_console_close(shutdown) if WINDOWS else None  # noqa: F841 (kept alive on purpose)
    try:
        database(pg, settings)
        started = True
        run([PYTHON, '-c', '''import os, psycopg
from psycopg.conninfo import make_conninfo
url = os.environ['VERTICALS_DATABASE_URL']
with psycopg.connect(make_conninfo(url, dbname='postgres'), autocommit=True) as conn:
    if not conn.execute("SELECT 1 FROM pg_database WHERE datname='verticals'").fetchone():
        conn.execute('CREATE DATABASE verticals')
'''], env=env)
        run([PYTHON, '-m', 'verticals.db.runner', 'up'], env=env)
        if not (STATE / 'seeded').exists():
            run([PYTHON, '-c', '''import os, pathlib, psycopg
with psycopg.connect(os.environ['VERTICALS_DATABASE_URL']) as conn:
    if not conn.execute('SELECT 1 FROM goals LIMIT 1').fetchone():
        conn.execute(pathlib.Path('verticals/db/seed_sample.sql').read_text(encoding='utf-8'))
'''], env=env)
            (STATE / 'seeded').touch()
        # Rebuild with this isolated instance's token, never a token from web/.env.local.
        run([NPM, '--prefix', 'web', 'run', 'build'], env=env)
        for command in [[str(PYTHON), '-m', 'verticals.api.app'],
                        [NPM, '--prefix', 'web', 'run', 'preview', '--', '--host', '127.0.0.1', '--port', '8088', '--strictPort']]:
            processes.append(spawn(command, env))
        for attempt in range(60):
            if any(p.poll() is not None for p in processes):
                raise RuntimeError('A local service exited during startup.')
            try:
                with urllib.request.urlopen('http://127.0.0.1:8088/healthz', timeout=1) as response:
                    if json.load(response).get('db') == 'ok':
                        break
            except (OSError, ValueError):
                time.sleep(0.5)
        else:
            raise RuntimeError('Local health check timed out.')
        print('Verticals ready: http://127.0.0.1:8088/ — isolated sample database. Ctrl-C stops; data stays.', flush=True)
        while all(p.poll() is None for p in processes):
            time.sleep(1)
        raise RuntimeError('A local service exited; stopping remaining services.')
    except KeyboardInterrupt:
        print('Stopping local Verticals; preserving database.')
    finally:
        shutdown()


if __name__ == '__main__':
    main()
