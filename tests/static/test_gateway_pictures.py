"""Pictures sent with a message in the conversation (docs/design/chat-pictures.md): the gateway keeps them under their hash,
the page reaches no file but a kept picture, and each agent gets them in its own shape, before the words."""

from __future__ import annotations

import base64
import hashlib
import importlib.util
import sys
import threading
import types
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
# A PNG of one white pixel.
PNG = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=")
PNG_URL = "data:image/png;base64," + base64.b64encode(PNG).decode()
PNG_NAME = hashlib.sha256(PNG).hexdigest()[:32] + ".png"


def _chat():
    spec = importlib.util.spec_from_file_location("verticals_desktop_chat", ROOT / "desktop" / "chat" / "chat.py")
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def _gateway(chat, state: Path):
    return chat.Chat(state, "http://127.0.0.1:1/mcp", "tok", watch_every=0)


def test_a_picture_is_kept_once_under_its_hash_and_found_again_by_that_name(tmp_path: Path) -> None:
    chat = _chat()
    gateway = _gateway(chat, tmp_path)
    assert gateway.keep_pictures([PNG_URL, PNG_URL]) == [PNG_NAME, PNG_NAME]
    assert [p.name for p in (tmp_path / "chat-pictures").iterdir()] == [PNG_NAME]
    assert gateway.picture_path(PNG_NAME).read_bytes() == PNG
    assert gateway.keep_pictures(None) == [] and gateway.keep_pictures([]) == []


def test_the_page_reaches_no_file_but_a_kept_picture(tmp_path: Path) -> None:
    chat = _chat()
    gateway = _gateway(chat, tmp_path)
    gateway.keep_pictures([PNG_URL])
    (tmp_path / "chat-sessions.json").write_text("{}")
    for name in ("../chat-sessions.json", "chat-sessions.json", PNG_NAME.replace(".png", ".svg"), "0" * 32 + ".png", None, 7):
        assert gateway.picture_path(name) is None, name


@pytest.mark.parametrize("url", [
    "data:image/svg+xml;base64," + base64.b64encode(b"<svg/>").decode(),
    "data:image/png," + base64.b64encode(PNG).decode(),
    "data:image/png;base64,not base64!",
    "data:image/png;base64,",
    "https://example.com/a.png",
    42,
])
def test_what_is_no_picture_of_a_kind_both_agents_read_is_refused(tmp_path: Path, url) -> None:
    chat = _chat()
    with pytest.raises(chat.ChatError):
        _gateway(chat, tmp_path).keep_pictures([url])
    assert not (tmp_path / "chat-pictures").exists()


def test_a_message_carries_twenty_pictures_at_most(tmp_path: Path) -> None:
    chat = _chat()
    with pytest.raises(chat.ChatError, match="20 pictures"):
        _gateway(chat, tmp_path).keep_pictures([PNG_URL] * 21)


class _Proc:
    def __init__(self, *args, **kwargs):
        self.sent, self.requests, self.done = [], [], threading.Event()

    def send(self, message):
        self.sent.append(message)

    def request(self, method, params):
        self.requests.append((method, params))
        self.done.set()
        return {"turn": {"id": "turn-1"}}


def test_claude_gets_each_picture_as_an_image_block_before_the_words(tmp_path: Path) -> None:
    chat = _chat()
    (tmp_path / PNG_NAME).write_bytes(PNG)
    agent = types.SimpleNamespace(proc=_Proc(), session_id="s1", streamed=True)
    chat.ClaudeAgent.send(agent, "What is this?", [tmp_path / PNG_NAME])
    content = agent.proc.sent[0]["message"]["content"]
    assert content == [{"type": "image", "source": {"type": "base64", "media_type": "image/png",
                                                     "data": base64.b64encode(PNG).decode()}},
                       {"type": "text", "text": "What is this?"}]
    chat.ClaudeAgent.send(agent, "And now?")
    assert agent.proc.sent[1]["message"]["content"] == [{"type": "text", "text": "And now?"}]


def test_codex_gets_each_picture_as_a_local_image_before_the_words(tmp_path: Path) -> None:
    chat = _chat()
    session = types.SimpleNamespace(settings=chat.clean_settings({"provider": "codex"}), turn_done=lambda *a: None)
    agent = types.SimpleNamespace(proc=_Proc(), s=session, settings=None, pending_mcp=set(), cwd=tmp_path,
                                  thread_id="t1", turn_id=None)
    chat.CodexAgent.send(agent, "What is this?", [tmp_path / PNG_NAME])
    assert agent.proc.done.wait(5)
    method, params = agent.proc.requests[0]
    assert method == "turn/start"
    assert params["input"] == [{"type": "localImage", "path": str(tmp_path / PNG_NAME)},
                               {"type": "text", "text": "What is this?", "text_elements": []}]
