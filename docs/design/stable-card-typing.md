# A stable task behind the conversation

Typing in the shared field still filters and highlights the board. A task opened in a window,
including its children, does not take those search highlights or the grey styling for context
ancestors. Its title and colour stay steady while the person writes in the conversation.

Changes to the task itself still update the window and its blurred copies. Opening the chat,
closing it and switching between a task and a document keep their existing transitions.

## Verification and remaining check

The regression test drives the real UI and gateway with synthetic data. It covers parent and
child title matches, unrelated text, deletion, paste, cursor movement and a newline; window
DOM stays stable, while a title edited in the window reaches the live card and its blurred
copies when the chat reopens. A separate control keeps board search highlighting enabled.

Chromium and Playwright WebKit recordings cover a green task with a document beside it,
normal motion and a narrower window with reduced motion and the dark system preference.
The current UI retains its light surface under that preference.

A separate native WKWebView loaded the same production bundle and captured 131 snapshots
while changing the draft. One snapshot showed a different blurred layer; the other differences
were at most one colour-channel level. This has not been established as a visible screen
flicker: capturing the actual native window failed (`could not create image from window`).
The installed application was not changed. Native visual acceptance remains open; this change
removes the demonstrated search-triggered repaint but is not proof that every compositor
flicker is resolved.

Synthetic evidence: [before](../screenshots/stable-card-typing/before.png),
[after](../screenshots/stable-card-typing/after.png),
[native normal](../screenshots/stable-card-typing/native-normal.png),
[native anomalous snapshot](../screenshots/stable-card-typing/native-anomaly.png).

An additional probe found that an external API rename does not refresh the already-open task
window through the board change feed. The current live-board reload fetches board rows, not
the open detail; that existing limitation is outside this highlighting change.
