# The chat's tone

The chat is black with white type by default: the field, its tags and notices, the balloons. That is 21:1, and some eyes
tire on it fast, migraine-prone ones first. The agent menu (the chevron on the agent's tag) has a Chat column with three
tones:

- **Black**, the default, as before.
- **Soft**: a mid grey (`#3e3e42`) with light grey type (`#dadade`), about 7.5:1.
- **Light**: a pale grey (`#e6e6e9`) with dark grey type (`#36363a`), about 9.5:1, edged by a hairline against the white
  board.

The tone is the chat's alone: it does not follow or change the app's light or dark appearance. Quiet text (dates under a
chip, the second line of an answer) stays at 4.5:1 or more in every tone. A pick applies at once, to every conversation,
and is saved in this browser; with no local storage it lasts until the page closes.

The colours are tokens (`--vt-chat-*`) in `web/src/theme.css`; `lib/chatTone.ts` sets `data-chat-tone` on the root.
