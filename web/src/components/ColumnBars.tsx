/* The period bars, | ||| | (web/src/components/ColumnBars.tsx, docs/design/column-bars.md): the column's own sign in place
   of ← • →. The three in the middle are one button, the middle bar dark, opening the mode; a side bar steps the column to
   the period before or after. A column paged away from today shows the middle bar alone, `|`, and its click goes back to
   today; the two light bars only fade, so every box stays where the hand finds it. */
import { cx, fallthrough, sx } from '../bind'
import { tracked } from '../tracked'
import { withDefaults } from '../setup'
import './columnBars.css'

interface Props {
  /** The column's kind, "Week": names the side bars. */
  kind: string
  /** What the mode shows, "Weeks": names the three bars. */
  plural: string
  /** The column's period no longer holds today: the sign is the single bar that goes back to it. */
  away?: boolean
  /** Where the words on the line end, in px from the column's left (ColumnLine): the left box grows no further. */
  wordsEnd?: number
  onStep?: (direction: -1 | 1) => void
  onMode?: () => void
  onToday?: () => void
}
const DEFAULTS = { away: false, wordsEnd: 0 }

export const ColumnBars = tracked(function ColumnBars(raw: Props & Record<string, unknown>) {
  const { kind, plural, away, wordsEnd, onStep, onMode, onToday, ...attrs } = withDefaults(raw, DEFAULTS)
  const noun = kind.toLowerCase()
  function onMiddle(): void {
    if (away) onToday?.()
    else onMode?.()
  }
  return (
    <div
      {...fallthrough({
        className: cx('column-bars', { 'column-bars--away': away }),
        style: sx({ '--words-end': `${wordsEnd}px` }),
        'data-cap': 'period-nav',
      }, attrs)}
    >
      <button
        className="column-bars__button column-bars__button--prev"
        type="button"
        aria-label={`Previous ${noun}`}
        data-cap="period-prev"
        onClick={() => onStep?.(-1)}
      ><span className="column-bars__mark"><i className="column-bars__bar"></i></span></button>
      <button
        className="column-bars__button column-bars__button--mode"
        type="button"
        aria-label={away ? 'Today' : `${plural} side by side`}
        data-cap={away ? 'period-today' : 'period-mode'}
        onClick={onMiddle}
      ><span className="column-bars__mark"><i className="column-bars__bar column-bars__bar--outer"></i><i className="column-bars__bar column-bars__bar--ink"></i><i className="column-bars__bar column-bars__bar--outer"></i></span></button>
      <button
        className="column-bars__button column-bars__button--next"
        type="button"
        aria-label={`Next ${noun}`}
        data-cap="period-next"
        onClick={() => onStep?.(1)}
      ><span className="column-bars__mark"><i className="column-bars__bar"></i></span></button>
    </div>
  )
})
