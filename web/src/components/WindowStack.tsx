/* The windows over the board: one in the centre, 720 px; a goal or a page a card
   32 px from the top, a document full height; the others at the sides, 200 px of each in view, stepped back to 94%,
   softer and lit less; beyond the neighbours they wait off the screen in the order they opened. Under the conversation
   the centre one steps back and blurs down its height. */
import { Fragment, type WheelEvent as ReactWheelEvent } from 'react'
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from '../reactivity'
import { AppIcon } from './AppIcon'
import { ProgressiveBlur } from './ProgressiveBlur'
import { GoalWindowBody } from './GoalWindowBody'
import { PageWindowBody } from './PageWindowBody'
import { DocWindowBody } from './DocWindowBody'
import { DocDeleteButton } from './DocDeleteButton'
import { agentChat } from '../lib/agentChat'
import { closeWindow, closeWindows, focusWindow, stepWindow, windows, type VtWindow } from '../lib/windows'
import { goalLight } from '../lib/look'
import { store } from '../store'
import { curve, reducedMotion, timing } from '../lib/motion'
import { cx, fallthrough, onRelease, sx } from '../bind'
import { tracked } from '../tracked'
import { useSetup } from '../setup'
import { TransitionGroup } from '../Transition'
import './windowStack.css'

type HeadPart = { text: string; act?: 'versions' }
type ElementRef = (el: HTMLElement | null) => () => void

export const WindowStack = tracked(function WindowStack(attrs: Record<string, unknown>) {
  const s = useSetup({}, () => {
    const room = reactive({ width: typeof innerWidth === 'number' ? innerWidth : 1440 })
    function onResize(): void { room.width = innerWidth }
    onMounted(() => window.addEventListener('resize', onResize))
    onBeforeUnmount(() => window.removeEventListener('resize', onResize))

    const WIDTH = computed(() => Math.min(720, room.width - 32))
    const SIDE = 200
    const GAP = 24

    function colorOf(win: VtWindow): string | null {
      return win.kind === 'goal' ? (store.state.goalDetail?.id === win.target ? store.state.goalDetail?.color ?? null : null) : null
    }
    function light(win: VtWindow) {
      return goalLight(colorOf(win))
    }

    /** Where a window stands for its place in the row relative to the one in front. */
    function place(index: number) {
      const k = index - windows.front
      const w = WIDTH.value
      const centre = (room.width - w) / 2
      if (k === 0) return { left: centre, origin: '50% 0', scale: agentChat.open ? 'var(--vt-step-back)' : '1', z: 286 }
      const shows = room.width >= w + 2 * SIDE + 2 * GAP ? SIDE : 0
      const stepped = w * 0.94 + GAP
      if (k < 0) {
        const right = shows - (-k - 1) * stepped - (shows ? 0 : GAP)
        return { left: right - w, origin: '100% 0', scale: 'var(--vt-step-side)', z: 285 }
      }
      const left = room.width - shows + (k - 1) * stepped + (shows ? 0 : GAP)
      return { left, origin: '0 0', scale: 'var(--vt-step-side)', z: 285 }
    }

    function mode(index: number): 'none' | 'under' | 'side' {
      if (index !== windows.front) return 'side'
      return agentChat.open ? 'under' : 'none'
    }

    const scrollers = ref<Record<string, HTMLElement | null>>({})
    const frames = ref<Record<string, HTMLElement | null>>({})

    /* Pop out (S3.P2.013): the window grows out of the row or link it was opened from. */
    watch(() => windows.origin, async (origin) => {
      if (!origin) return
      await nextTick()
      const el = frames.value[origin.key]
      windows.origin = null
      if (!el) return
      const to = el.getBoundingClientRect()
      if (reducedMotion()) {
        el.animate([{ opacity: 0 }, { opacity: 1 }], timing('fade', 'large'))
        return
      }
      const from = origin.rect
      const scale = Math.max(0.1, from.width / to.width)
      el.animate([
        { transform: `translate(${from.left - to.left}px, ${from.top - to.top}px) scale(${scale})`, opacity: 0.6 },
        { transform: 'none', opacity: 1 },
      ], { duration: 400, easing: curve('large') })
    })

    /* A two-finger swipe moves one window, when the window under it can't scroll that way (S3.P3.010, .025). */
    let swiped = 0
    let swipeAt = 0
    let swipeUsed = false
    function onWheel(event: WheelEvent): void {
      if (Math.abs(event.deltaX) <= Math.abs(event.deltaY)) return
      const body = (event.target as HTMLElement).closest<HTMLElement>('.vt-window__body, .doc-window, .page-window')
      if (body && body.scrollWidth > body.clientWidth) return
      const now = performance.now()
      if (now - swipeAt > 300) {
        swiped = 0
        swipeUsed = false
      }
      swipeAt = now
      if (swipeUsed) return
      swiped += event.deltaX
      if (Math.abs(swiped) < 60) return
      stepWindow(swiped > 0 ? 1 : -1)
      swipeUsed = true
    }

    /* A document's head: its facts, then its versions, which open history in Documents. */
    function headParts(win: VtWindow): HeadPart[] {
      const parts: HeadPart[] = (win.facts ?? []).map((text) => ({ text }))
      if ((win.versions ?? 0) > 1) parts.push({ text: `${win.versions} versions`, act: 'versions' })
      return parts
    }
    function onHeadAct(win: VtWindow): void {
      closeWindow(win.key)
      void store.openHistoryOf(win.target)
    }

    function onWindowClick(index: number, event: MouseEvent): void {
      if (index !== windows.front) {
        event.preventDefault()
        event.stopPropagation()
        focusWindow(index)
        return
      }
      if (!agentChat.open) return
      agentChat.open = false
      const field = document.activeElement
      if (field instanceof HTMLElement && field.closest('[data-cap="search-input"]')) field.blur()
    }

    /* Ref callbacks, one per window so React hands each element over once; they let go as the window starts to leave,
       as Vue's do. A window's click handler is a native capture listener on it, there for as long as the element is: React's
       would also hear clicks in the popovers its content teleports. It passes the window's place in the row as last drawn. */
    const drawnAt = new Map<string, number>()
    const frameRefs = new Map<string, ElementRef>()
    function frameRef(key: string): ElementRef {
      let set = frameRefs.get(key)
      if (!set) {
        const onClick = (event: MouseEvent) => onWindowClick(drawnAt.get(key) ?? -1, event)
        set = (el) => {
          frames.value[key] = el
          if (el) {
            el.addEventListener('click', onClick, true)
            onRelease(el, () => {
              if (frames.value[key] === el) frames.value[key] = null
            })
          }
          return () => {
            if (frames.value[key] === el) frames.value[key] = null
          }
        }
        frameRefs.set(key, set)
      }
      return set
    }
    const scrollerRefs = new Map<string, ElementRef>()
    function scrollerRef(key: string): ElementRef {
      let set = scrollerRefs.get(key)
      if (!set) {
        set = (el) => {
          scrollers.value[key] = el
          if (el) onRelease(el, () => {
            if (scrollers.value[key] === el) scrollers.value[key] = null
          })
          return () => {
            if (scrollers.value[key] === el) scrollers.value[key] = null
          }
        }
        scrollerRefs.set(key, set)
      }
      return set
    }

    return {
      WIDTH, scrollers, light, place, mode, onWheel, headParts, onHeadAct, drawnAt, frameRef, scrollerRef,
    }
  })

  const WIDTH = s.WIDTH.value
  return (
    <div {...fallthrough({
      className: 'window-stack',
      'data-role': 'window-stack',
      onWheel: (event: ReactWheelEvent) => s.onWheel(event.nativeEvent),
    }, attrs)}>
      <TransitionGroup name="vt-window">
        {windows.list.map((win, index) => {
          s.drawnAt.set(win.key, index)
          const front = index === windows.front
          const at = s.place(index)
          const lit = s.light(win)
          return (
            <section
              key={win.key}
              ref={s.frameRef(win.key)}
              className={cx('vt-window vt-shadow', {
                'vt-shadow--lit': !!lit,
                'vt-shadow--side': !front,
                'vt-shadow--back': front && agentChat.open,
                'vt-window--front': front,
                'vt-window--side': !front,
                'vt-window--full': win.kind === 'doc',
              })}
              data-window={win.kind}
              data-key={win.key}
              aria-label={front ? win.title : `${win.title}. Bring to the centre`}
              style={sx({
                left: `${at.left}px`,
                width: `${WIDTH}px`,
                zIndex: at.z,
                transformOrigin: at.origin,
                '--vt-window-scale': at.scale,
                ...(lit ?? {}),
              })}
            >
              {win.kind !== 'goal' ? (
                <header className={cx('vt-window__head', {
                  'vt-window__head--doc': win.kind === 'doc',
                  'is-titled': win.kind === 'doc' && win.titled,
                })}>
                  {win.kind === 'doc' ? (
                    <>
                      <AppIcon className="vt-window__icon" name="file" size={16} />
                      <p className="vt-window__facts" data-role="doc-facts">
                        {win.titled ? (
                          <>
                            <b className="vt-window__title" data-role="doc-head-title">{win.title}</b>
                            <span className="vt-window__sep">·</span>
                          </>
                        ) : null}
                        {s.headParts(win).map((part, i) => (
                          <Fragment key={i}>
                            {i ? <span className="vt-window__sep">·</span> : null}
                            {part.act ? (
                              <button type="button" className="vt-window__act" data-role={`doc-${part.act}`}
                                onClick={(event) => { event.stopPropagation(); s.onHeadAct(win) }}>{part.text}</button>
                            ) : part.text}
                          </Fragment>
                        ))}
                      </p>
                      <DocDeleteButton id={win.target} />
                    </>
                  ) : (
                    <h2 className="vt-window__name" title={win.title}>{win.title}</h2>
                  )}
                  {win.kind === 'page' ? (
                    <a className="vt-window__button" href={win.target} target="_blank" rel="noreferrer" aria-label="Open in Chrome"
                      title="Open in Chrome"><AppIcon name="arrow-up-right" size={16} /></a>
                  ) : null}
                  <button type="button" className="vt-window__button" aria-label="Close"
                    onClick={(event) => { event.stopPropagation(); closeWindow(win.key) }}><AppIcon name="x" size={16} /></button>
                </header>
              ) : (
                <button type="button" className="vt-window__button vt-window__close" aria-label="Back to the board"
                  data-role="window-close" onClick={(event) => { event.stopPropagation(); closeWindows() }}>
                  <AppIcon name="x" size={16} />
                </button>
              )}
              <div ref={s.scrollerRef(win.key)} className="vt-window__body">
                <ProgressiveBlur mode={s.mode(index)} pale={lit?.['--vt-pale'] ?? '245, 245, 247'}
                  scroller={s.scrollers.value[win.key]}>
                  {win.kind === 'goal' ? (
                    <GoalWindowBody id={win.target} front={front} />
                  ) : win.kind === 'doc' ? (
                    <DocWindowBody id={win.target} part={win.part} />
                  ) : (
                    <PageWindowBody url={win.target} />
                  )}
                </ProgressiveBlur>
              </div>
            </section>
          )
        })}
      </TransitionGroup>
    </div>
  )
})
