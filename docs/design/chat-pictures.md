# Pictures in the conversation

The person is talking with the agent about a task and wants to show it something: a screenshot of what they see, a page of
another app. They drop or paste the picture as they would for a report and ask about it in the same words.

## Behaviour

- **With the conversation open, pictures go to the agent.** They stack above the field as a report's do
  (`bug-reports.md`), the field keeps the conversation's caption, and ↵ sends the words with the pictures as one message.
  With no conversation open, pictures and words are a report, as before.
- **Your balloon shows them,** above your words, at once, from the files you dropped; once the gateway has them, from its own
  copies, so they stay after a restart.
- **Both agents see them:** Claude Code gets each as an image block before the words, Codex as a local image. A picture is
  drawn smaller first (a clear screenshot, at most about a megabyte), as a report's are.
- **The gateway keeps them** in `chat-pictures/` of the app's state, named by their hash, so the same picture sent twice is
  one file, and the page reads them back only by such a name. A message carries 20 at most, each a PNG, JPEG, WebP or GIF
  of 5 MB or less.
- An agent picked up again from the transcript (another agent, a lost session) reads `[N pictures attached]` where they were.

## Done when

UI tests: with the conversation open, a dropped picture and words make one message: no report, no task, the picture in
your balloon and in what the agent received. Gateway tests: what each agent is sent, and that the page reaches no file but a
kept picture.
