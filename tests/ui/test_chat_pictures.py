"""Pictures in the conversation (docs/design/chat-pictures.md): with the conversation open, a picture dropped on the window and
the words typed after it are one message to the agent. No report and no task come of it; the picture shows in your balloon and
reaches the agent (the stand-in agent says what it got, `[pictures]` in tests/ui/fake_agent/claude). With the conversation
closed, the same picture and words are a report, as before (tests/ui/test_report.py). F2 behind the desktop gateway."""

from __future__ import annotations

import re

from playwright.sync_api import Page, expect

from tests.ui.conftest import UiSession
from tests.ui.report_support import FIELD, ITEM, drop, files
from tests.ui.test_agent_conversation import (  # noqa: F401  (the fixture)
    BALLOON, CONVERSATION, _answered, _send, close_the_conversation, ui_agent,
)
from tests.ui.test_report import _reports

YOURS = f'{BALLOON}[data-who="you"]'
PICTURE = ".agent-balloon__picture"


def _open_conversation(page: Page) -> None:
    page.wait_for_selector(".goal-card__row")
    _send(page, "Hello [say] Hi")
    _answered(page, "Hi")
    expect(page.locator(CONVERSATION)).to_have_count(1)


def test_with_the_conversation_open_a_picture_goes_to_the_agent_with_the_words(ui_agent: UiSession) -> None:  # noqa: F811
    session = ui_agent
    page = session.page
    _open_conversation(page)

    drop(page, files(1))
    expect(page.locator(ITEM)).to_have_count(1)
    # The field stays the conversation's: the words go to the agent, and the board is not narrowed by them.
    expect(page.locator(CONVERSATION)).to_have_count(1)
    page.locator(FIELD).fill("What is on it? [pictures]")
    expect(page.get_by_role("button", name="Send to the agent")).to_have_count(1)
    page.keyboard.press("Enter")

    _answered(page, "I see 1 picture: image/jpeg")
    mine = page.locator(YOURS).last
    expect(mine).to_contain_text("What is on it? [pictures]")
    expect(mine.locator(PICTURE)).to_have_count(1)
    # Once the gateway has it, the balloon shows the gateway's copy, which stays after a restart.
    expect(mine.locator(PICTURE)).to_have_attribute("src", re.compile(r"/__chat/pictures/[0-9a-f]{32}\.jpg$"))
    page.wait_for_function("img => img.complete && img.naturalWidth > 0", arg=mine.locator(PICTURE).element_handle())
    src = mine.locator(PICTURE).get_attribute("src")
    fetched = page.evaluate("(src) => fetch(src).then(r => [r.status, r.headers.get('content-type')])", src)
    assert fetched == [200, "image/jpeg"]

    # The stack above the field is empty, and nothing was made of it: no report, no question of which kind it is.
    expect(page.locator(ITEM)).to_have_count(0)
    assert not [r for r in session.request_log if "/__chat/report-kind" in r["url"]]
    assert _reports(session.backend.dsn) == []


def test_with_the_conversation_closed_the_picture_and_words_are_a_report_again(ui_agent: UiSession) -> None:  # noqa: F811
    session = ui_agent
    page = session.page
    _open_conversation(page)
    close_the_conversation(page)

    drop(page, files(1))
    expect(page.locator(ITEM)).to_have_count(1)
    page.locator(FIELD).fill("I dragged the card to Friday and it jumped back")
    expect(page.get_by_role("button", name="Make the report")).to_have_count(1)
    page.keyboard.press("Enter")
    expect(page.locator('[data-role="circle-answer"]')).to_contain_text("Report made with 1 picture", timeout=15000)
    assert len(_reports(session.backend.dsn)) == 1
