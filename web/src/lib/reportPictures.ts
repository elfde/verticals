// Pictures waiting above the field (docs/design/bug-reports.md). A picture dropped on the window or pasted stands above the
// field as a goal let go over it does (MovingStack.tsx hosts both), and the field widens to hold them. Past a few they
// overlap, so any number still fits; the one under the pointer lifts out, bigger (FieldPictures.tsx). They wait until the
// person says what they see and what they want: then they are the report's pictures (lib/report.ts). With the conversation
// open they are the message's instead, and go to the agent with the words (docs/design/chat-pictures.md).
import { computed, reactive } from '../reactivity'
import { agentChat } from './agentChat'

export interface FieldPicture {
  id: string
  name: string
  file: File
  /** An object URL of the file: what the stack shows. The report is made from the file itself. */
  url: string
  /** The picture's own size, which gives the stack the shape of each one. */
  width: number
  height: number
}

export const pictures = reactive({
  list: [] as FieldPicture[],
  /** The one picked up with Backspace or a tap: lifted as the pointer lifts one. */
  picked: null as string | null,
  /** A file is being dragged over the window: the field opens to take it. */
  over: false,
  /** The report is being made from them. */
  sending: false,
})

/** The field is a report's: pictures wait above it, and no conversation is open to take them. */
export const reporting = computed(() => pictures.list.length > 0 && !agentChat.open)

/** The height of a picture in the stack, the width it takes at that height (36 to 112: a wide screenshot is 90, a phone's
 *  36) and the gap between two; the stack never asks the field for more than `STACK_MAX`, and past that they overlap. */
export const PICTURE_HEIGHT = 56
export const PICTURE_GAP = 8
const PICTURE_MIN = 36
const PICTURE_MAX = 112
export const STACK_MAX = 440

export const tileWidth = (picture: { width: number; height: number }): number =>
  Math.max(PICTURE_MIN, Math.min(PICTURE_MAX, Math.round(PICTURE_HEIGHT * picture.width / Math.max(1, picture.height))))

/** The width the stack would take if none overlapped. */
export function stackWidth(list: { width: number; height: number }[]): number {
  return list.reduce((sum, picture) => sum + tileWidth(picture), 0) + PICTURE_GAP * Math.max(0, list.length - 1)
}

/** The width the stack takes in the field: all of it, up to `STACK_MAX`; past that the pictures overlap. */
export function stackRoom(list: { width: number; height: number }[]): number {
  return Math.min(STACK_MAX, stackWidth(list))
}

const TYPES = /^image\/(png|jpe?g|gif|webp|heic|heif|tiff|bmp)$/i
const NAMES = /\.(png|jpe?g|gif|webp|heic|heif|tiff?|bmp)$/i
/** A picture the page can show and the report can hold. An SVG is a document, not a picture. */
export const looksLikePicture = (file: File): boolean => (file.type ? TYPES.test(file.type) : NAMES.test(file.name))

async function sizeOf(file: File): Promise<{ width: number; height: number } | null> {
  try {
    const bitmap = await createImageBitmap(file)
    const size = { width: bitmap.width, height: bitmap.height }
    bitmap.close()
    return size.width > 0 && size.height > 0 ? size : null
  } catch {
    return null
  }
}

/** Pictures onto the stack, in the order they came; a file that is no picture, or that cannot be read as one, is left. */
export async function addFiles(files: Iterable<File>): Promise<number> {
  const found = [...files].filter(looksLikePicture)
  const sizes = await Promise.all(found.map(sizeOf))
  let added = 0
  found.forEach((file, index) => {
    const size = sizes[index]
    if (!size) return
    pictures.list.push({ id: crypto.randomUUID(), name: file.name || 'picture', file, url: URL.createObjectURL(file), ...size })
    added += 1
  })
  return added
}

export function removePicture(id: string): void {
  const at = pictures.list.findIndex((picture) => picture.id === id)
  if (at < 0) return
  URL.revokeObjectURL(pictures.list[at]!.url)
  pictures.list.splice(at, 1)
  if (pictures.picked === id) pictures.picked = null
}

/** The pictures' files, in the order they came, and the stack emptied: they go with a message. */
export function takePictures(): File[] {
  const files = pictures.list.map((picture) => picture.file)
  clearPictures()
  return files
}

export function clearPictures(): void {
  for (const picture of pictures.list) URL.revokeObjectURL(picture.url)
  pictures.list.splice(0)
  pictures.picked = null
}

/** A tap picks it; a tap on the one picked lets go (as a goal waiting above the field is picked, lib/moving.ts). */
export function tapPicture(id: string): void {
  pictures.picked = pictures.picked === id ? null : id
}

/** Backspace on an empty field: pick the last picture; again, take it off and pick the one before (lib/moving.ts's way). */
export function picturesBackspace(): boolean {
  if (!pictures.list.length) return false
  const picked = pictures.picked
  if (picked) {
    const at = pictures.list.findIndex((picture) => picture.id === picked)
    removePicture(picked)
    pictures.picked = pictures.list[Math.max(0, at - 1)]?.id ?? null
    return true
  }
  pictures.picked = pictures.list[pictures.list.length - 1]!.id
  return true
}

/** Esc lets go of the pick; with nothing picked it sends all the pictures away. */
export function picturesEscape(): boolean {
  if (pictures.picked) {
    pictures.picked = null
    return true
  }
  if (!pictures.list.length) return false
  clearPictures()
  return true
}

// ---------------------------------------------------------------- how they arrive

const hasFiles = (data: DataTransfer | null): boolean => !!data && [...data.types].includes('Files')
const picturesIn = (data: DataTransfer | null): File[] => [...(data?.files ?? [])].filter(looksLikePicture)

/** The field's text area takes the keys, so the words follow the pictures at once. It opens no conversation. */
function takeTheKeys(): void {
  const active = document.activeElement
  if (active instanceof HTMLElement && active.closest('textarea, input, [contenteditable="true"]')) return
  document.querySelector<HTMLTextAreaElement>('[data-cap="search-input"] textarea')?.focus({ preventScroll: true })
}

/** Pictures arrive by drop anywhere on the window, or paste: the field takes the keys. Whatever else is dragged over the
 *  window is kept from opening in it (a file dropped on a web view would replace the app with it). Returns the way to stop. */
export function watchPictureInput(): () => void {
  let depth = 0
  const dragged = (event: DragEvent): boolean => {
    if (!hasFiles(event.dataTransfer)) return false
    event.preventDefault()
    return true
  }
  const onEnter = (event: DragEvent): void => {
    if (!dragged(event)) return
    depth += 1
    pictures.over = true
  }
  const onOver = (event: DragEvent): void => {
    if (dragged(event) && event.dataTransfer) event.dataTransfer.dropEffect = 'copy'
  }
  const onLeave = (event: DragEvent): void => {
    if (!hasFiles(event.dataTransfer)) return
    depth = Math.max(0, depth - 1)
    if (!depth) pictures.over = false
  }
  const onDrop = (event: DragEvent): void => {
    if (!dragged(event)) return
    depth = 0
    pictures.over = false
    void addFiles(picturesIn(event.dataTransfer)).then((added) => { if (added) takeTheKeys() })
  }
  const onPaste = (event: ClipboardEvent): void => {
    const files = picturesIn(event.clipboardData)
    // A picture alone is a picture; anything with words pastes as words.
    if (!files.length || event.clipboardData?.getData('text/plain')) return
    const target = event.target instanceof Element ? event.target : null
    const editor = target?.closest('input, textarea, select, [contenteditable="true"]')
    // Another editor has its own paste (a document's body drops a picture from elsewhere on purpose); the field's is ours.
    if (editor && !editor.closest('[data-cap="search-input"]')) return
    event.preventDefault()
    void addFiles(files).then((added) => { if (added) takeTheKeys() })
  }
  window.addEventListener('dragenter', onEnter, true)
  window.addEventListener('dragover', onOver, true)
  window.addEventListener('dragleave', onLeave, true)
  window.addEventListener('drop', onDrop, true)
  window.addEventListener('paste', onPaste, true)
  return () => {
    window.removeEventListener('dragenter', onEnter, true)
    window.removeEventListener('dragover', onOver, true)
    window.removeEventListener('dragleave', onLeave, true)
    window.removeEventListener('drop', onDrop, true)
    window.removeEventListener('paste', onPaste, true)
  }
}
