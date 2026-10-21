"""Hiding a task's steps changes its view without changing the tasks themselves."""

import httpx
from playwright.sync_api import Locator, Page, expect

from tests.ui.conftest import UiSession, activate_column


def _family(session: UiSession) -> tuple[str, str]:
    headers = {"Authorization": f"Bearer {session.backend.token}"}
    url = f"{session.backend.base_url}/api/goals"
    parent = httpx.post(url, headers=headers, json={"title": "SYN parent with steps",
                       "vertical": "month", "anchor_date": "2026-08-08",
                       "body": "SYN notes remain available."}).json()["id"]
    child = httpx.post(url, headers=headers, json={"title": "SYN child to hide", "parent_id": parent,
                      "vertical": "month", "anchor_date": "2026-08-08"}).json()["id"]
    session.page.reload()
    activate_column(session.page, "month")
    return parent, child


def _toggle(page: Page, parent: str, *, expanded: bool) -> Locator:
    """Hide ends a shown list; Show stands under the title of a hidden one."""
    return page.locator(f'[data-subtasks-of="{parent}"][aria-expanded="{str(expanded).lower()}"]')


def _below(lower: Locator, upper: Locator) -> None:
    a, b = upper.bounding_box(), lower.bounding_box()
    assert a is not None and b is not None
    assert b["y"] >= a["y"] + a["height"] - 1, (a, b)


def test_subtasks_can_be_hidden_and_shown_without_changing_the_family(ui_f2: UiSession) -> None:
    parent, child = _family(ui_f2)
    page = ui_f2.page
    hide = _toggle(page, parent, expanded=True)
    show = _toggle(page, parent, expanded=False)
    expect(page.locator(f'.goal-card[data-goal-id="{child}"]')).to_be_visible()
    expect(show).to_have_count(0)
    _below(hide, page.locator(f'.goal-card[data-goal-id="{child}"]').first)
    hide.click()
    expect(show).to_be_visible()
    expect(hide).to_have_count(0)
    expect(page.locator(f'.goal-card[data-goal-id="{child}"]')).not_to_be_visible()
    expect(page.locator('#goal-detail')).to_have_count(0)
    data = httpx.get(f"{ui_f2.backend.base_url}/api/goals/{parent}",
                     headers={"Authorization": f"Bearer {ui_f2.backend.token}"}).json()
    assert any(step['id'] == child for step in data['children'])
    _below(show, page.locator(f'.goal-card[data-goal-id="{parent}"] .goal-card__title').first)
    show.focus()
    page.keyboard.press('Enter')
    expect(hide).to_be_focused()
    expect(page.locator(f'.goal-card[data-goal-id="{child}"]')).to_be_visible()


def test_hidden_subtasks_stay_hidden_on_reload_and_leave_notes_accessible(ui_f2: UiSession) -> None:
    parent, child = _family(ui_f2)
    page = ui_f2.page
    _toggle(page, parent, expanded=True).click()
    page.reload()
    activate_column(page, "month")
    card = page.locator(f'.goal-card[data-goal-id="{parent}"]').first
    expect(_toggle(page, parent, expanded=False)).to_be_visible()
    card.locator('.goal-card__title').click()
    expect(page.locator('#goal-detail')).to_contain_text('SYN notes remain available.')
    expect(page.locator(f'.goal-card[data-goal-id="{child}"]')).not_to_be_visible()
    _toggle(page, parent, expanded=False).click()
    expect(page.locator(f'.goal-card[data-goal-id="{child}"]')).to_be_visible()
    _below(_toggle(page, parent, expanded=True), page.locator(f'.goal-card[data-goal-id="{child}"]').first)
    expect(page.locator('#goal-detail')).to_contain_text('SYN notes remain available.')
