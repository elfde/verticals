"""The chat's tone (docs/design/chat-tone.md): black, soft or light, picked in the agent menu and kept across reloads; every
tone keeps the chat's words readable in both appearances, and the softer two give less contrast than black."""
from pathlib import Path

import psycopg
from playwright.sync_api import expect

from tests.ui.conftest import UiSession
from tests.ui.test_agent_conversation import AGENT_BALLOON, BALLOON, _answered, _send, ui_agent  # noqa: F401
from tests.ui.test_system_theme import _channels, _contrast, _rgb
from verticals.core import docs

SHOTS = Path(__file__).resolve().parents[2] / "artifacts" / "chat-tone"
TONES = ("black", "soft", "light")


def _open_menu(page):
    page.locator(".circle-field__shape").hover()
    page.locator(".agent-tag").click()
    expect(page.locator(".agent-picker")).to_be_visible()


def test_tone_is_picked_in_the_agent_menu_and_kept(ui_agent: UiSession):
    page = ui_agent.page
    page.wait_for_selector(".goal-card__row")
    assert page.evaluate("document.documentElement.dataset.chatTone") is None
    _open_menu(page)
    choices = page.locator(".agent-picker__choice[data-chat-tone]")
    expect(choices).to_have_count(3)
    expect(page.locator('.agent-picker__choice[data-chat-tone="black"]')).to_have_attribute("aria-pressed", "true")
    black = _rgb(page, ".circle-field__surface")
    page.locator('.agent-picker__choice[data-chat-tone="light"]').click()
    expect(page.locator('.agent-picker__choice[data-chat-tone="light"]')).to_have_attribute("aria-pressed", "true")
    assert page.evaluate("document.documentElement.dataset.chatTone") == "light"
    assert _rgb(page, ".circle-field__surface") != black
    SHOTS.mkdir(parents=True, exist_ok=True)
    page.screenshot(path=str(SHOTS / "menu.png"), animations="disabled")
    page.reload()
    page.wait_for_selector(".goal-card__row")
    assert page.evaluate("document.documentElement.dataset.chatTone") == "light"
    _open_menu(page)
    page.locator('.agent-picker__choice[data-chat-tone="black"]').click()
    assert page.evaluate("document.documentElement.dataset.chatTone") is None
    assert _rgb(page, ".circle-field__surface") == black


def test_every_tone_keeps_the_conversation_readable(ui_agent: UiSession):
    with psycopg.connect(ui_agent.backend.dsn, autocommit=True) as conn:
        doc_id = docs.create(conn, owner="t1", path="syn-tone/notes.md", title="SYN Tone notes",
                             body="Synthetic document linked by the agent.").doc.id
    page = ui_agent.page
    page.wait_for_selector(".goal-card__row")
    _send(page, f"SYN show me the notes [say] Your notes are ready.\n[SYN Tone notes](#doc/{doc_id})")
    _answered(page, "Your notes are ready.")
    you = f'{BALLOON}[data-who="you"]'
    SHOTS.mkdir(parents=True, exist_ok=True)
    for scheme in ("light", "dark"):
        page.emulate_media(color_scheme=scheme)
        black = None
        for tone in TONES:
            page.evaluate("tone => tone === 'black' ? delete document.documentElement.dataset.chatTone"
                          " : document.documentElement.dataset.chatTone = tone", tone)
            words = _contrast(page, AGENT_BALLOON, AGENT_BALLOON)
            for part in (".doc-chip__name", ".doc-chip__when"):
                assert _contrast(page, f"{AGENT_BALLOON} {part}", AGENT_BALLOON) >= 4.5, (scheme, tone, part)
            assert _contrast(page, you, you) >= 4.5, (scheme, tone)
            assert _contrast(page, ".circle-tag", ".circle-tag") >= 4.5, (scheme, tone)
            if tone == "black":
                black = words
            else:
                assert 4.5 <= words < black, (scheme, tone, words, black)
                assert max(_channels(_rgb(page, AGENT_BALLOON))[:3]) > 40, (scheme, tone, "still black")
            page.screenshot(path=str(SHOTS / f"{scheme}-{tone}.png"), animations="disabled")
