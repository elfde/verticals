"""The chat's tone (docs/design/chat-tone.md): black or soft, picked in the agent menu and kept across reloads; the closed
circle and its tags stay as they are, the open chat takes the tone; soft keeps every word readable in both appearances, with
less contrast than black's white in the light one."""
from pathlib import Path

import psycopg
from playwright.sync_api import expect

from tests.ui.conftest import UiSession
from tests.ui.test_agent_conversation import AGENT_BALLOON, BALLOON, _answered, _send, ui_agent  # noqa: F401
from tests.ui.test_system_theme import _contrast, _rgb
from tests.ui.views import FIELD
from verticals.core import docs

SHOTS = Path(__file__).resolve().parents[2] / "artifacts" / "chat-tone"
SOFT_INK = "rgb(229, 229, 229)"


def _open_menu(page):
    page.locator(".circle-field__shape").hover()
    page.locator(".agent-tag").click()
    expect(page.locator(".agent-picker")).to_be_visible()


def test_tone_is_picked_in_the_agent_menu_and_kept(ui_agent: UiSession):
    page = ui_agent.page
    page.wait_for_selector(".goal-card__row")
    assert page.evaluate("document.documentElement.dataset.chatTone") is None
    _open_menu(page)
    expect(page.locator(".agent-picker__choice[data-chat-tone]")).to_have_count(2)
    expect(page.locator('.agent-picker__choice[data-chat-tone="black"]')).to_have_attribute("aria-pressed", "true")
    page.locator('.agent-picker__choice[data-chat-tone="soft"]').click()
    expect(page.locator('.agent-picker__choice[data-chat-tone="soft"]')).to_have_attribute("aria-pressed", "true")
    SHOTS.mkdir(parents=True, exist_ok=True)
    page.screenshot(path=str(SHOTS / "menu.png"), animations="disabled")
    page.reload()
    page.wait_for_selector(".goal-card__row")
    assert page.evaluate("document.documentElement.dataset.chatTone") == "soft"
    # Closed, the tags keep their white; open, the field's words take the tone.
    expect(page.locator(".circle-tag").first).to_have_css("color", "rgb(255, 255, 255)")
    page.keyboard.press("Control+k")
    page.locator(FIELD).fill("SYN soft words")
    expect(page.locator(FIELD)).to_have_css("color", SOFT_INK)
    expect(page.locator(".circle-field__surface")).to_have_css("background-color", "rgb(0, 0, 0)")
    page.locator(FIELD).fill("")
    page.keyboard.press("Escape")
    _open_menu(page)
    page.locator('.agent-picker__choice[data-chat-tone="black"]').click()
    assert page.evaluate("document.documentElement.dataset.chatTone") is None


def test_soft_keeps_the_conversation_readable(ui_agent: UiSession):
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
        page.evaluate("delete document.documentElement.dataset.chatTone")
        black = _contrast(page, AGENT_BALLOON, AGENT_BALLOON)
        page.evaluate("document.documentElement.dataset.chatTone = 'soft'")
        assert _rgb(page, AGENT_BALLOON, "color") == SOFT_INK, scheme
        assert _rgb(page, AGENT_BALLOON) == "rgb(0, 0, 0)", scheme
        if scheme == "light":
            assert _contrast(page, AGENT_BALLOON, AGENT_BALLOON) < black
        for part in (".doc-chip__name", ".doc-chip__when"):
            assert _contrast(page, f"{AGENT_BALLOON} {part}", AGENT_BALLOON) >= 4.5, (scheme, part)
        assert _contrast(page, you, you) >= 4.5, scheme
        page.screenshot(path=str(SHOTS / f"{scheme}-soft.png"), animations="disabled")
