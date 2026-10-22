# The chat's tone

The chat is black with white type by default: the field, its tags, the balloons. That is 21:1, and some eyes tire on it
fast, migraine-prone ones first. The agent menu (the chevron on the agent's tag) has a Chat column with two tones:

- **Black**, the default, as before.
- **Soft**: the same black with light grey type (`#e5e5e5`) whatever the app's appearance, about 16:1 in the light
  appearance (the dark one already gives black's type `#d0d0d4`). The glare comes from the type, so the type is what gives.

Tinted and grey grounds were tried first (a grey, a blue graphite, a warm paper, the board's own greys) and read as
foreign to the chat; it keeps its black.

Soft dresses the chat only while it is open: the conversation, and the field once it opens or while the conversation
stands. Closed, the circle and its tags stay as they are, and so does the undo notice that takes the circle's place.

Quiet text (dates under a chip, the second line of an answer) stays at 4.5:1 or more. A pick applies at once, to every
conversation, and is saved in this browser; with no local storage it lasts until the page closes.

The colours are tokens (`--vt-chat-*`) in `web/src/theme.css`; `lib/chatTone.ts` sets `data-chat-tone` on the root.
