/* The chat's colours (docs/design/chat-tone.md): black, the default, or a softer grey or light ground for eyes that tire
   on white type over black. Picked in the agent menu, kept in this browser, and drawn by theme.css from
   `data-chat-tone` on the root. */
import { reactive } from '../reactivity'

export type ChatTone = 'black' | 'soft' | 'light'

export const CHAT_TONES: readonly { value: ChatTone; label: string }[] = [
  { value: 'black', label: 'Black' },
  { value: 'soft', label: 'Soft' },
  { value: 'light', label: 'Light' },
]

const KEY = 'verticals:chat-tone'

function saved(): ChatTone {
  try {
    const value = localStorage.getItem(KEY)
    return CHAT_TONES.some((tone) => tone.value === value) ? value as ChatTone : 'black'
  } catch {
    return 'black'
  }
}

function draw(tone: ChatTone): void {
  if (tone === 'black') document.documentElement.removeAttribute('data-chat-tone')
  else document.documentElement.setAttribute('data-chat-tone', tone)
}

export const chatTone = reactive({ value: saved() })
draw(chatTone.value)

export function setChatTone(tone: ChatTone): void {
  chatTone.value = tone
  draw(tone)
  try { localStorage.setItem(KEY, tone) } catch { /* private mode: the tone lasts until the page closes */ }
}
