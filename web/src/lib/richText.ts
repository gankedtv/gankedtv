/**
 * A small markdown subset for the profile bio. Returns a typed tree, never an HTML string, so
 * `RichText.vue` builds real elements — that is what keeps the repo's "no `v-html`" invariant,
 * and with no markup generated there is nothing to sanitise.
 */

export interface TextNode {
  t: 'text'
  v: string
}

export interface MarkNode {
  t: 'strong' | 'em'
  v: string
}

export interface LinkNode {
  t: 'link'
  href: string
  v: string
}

export type Inline = TextNode | MarkNode | LinkNode

export interface ParagraphBlock {
  t: 'p'
  /** One entry per soft line break; the renderer joins them with `<br>`. */
  lines: Inline[][]
}

export interface ListBlock {
  t: 'ul' | 'ol'
  items: Inline[][]
}

export type Block = ParagraphBlock | ListBlock

// Bound the render tree against pathological input; the bio is 500 chars, so real ones don't.
const MAX_BLOCKS = 60
const MAX_LIST_ITEMS = 40
const MAX_LINES_PER_PARAGRAPH = 40

const BULLET = /^[-*]\s+(.*)$/
const NUMBERED = /^\d{1,3}[.)]\s+(.*)$/

/** http/https only. Scheme-less is rejected too, so `example.com` can't fake an internal route. */
export function safeLinkUrl(url: string): string | null {
  try {
    const parsed = new URL(url)
    return parsed.protocol === 'https:' || parsed.protocol === 'http:' ? url : null
  } catch {
    return null
  }
}

export function parseRichText(source: string | null | undefined): Block[] {
  if (!source) return []

  const lines = source.replace(/\r\n?/g, '\n').split('\n')
  const blocks: Block[] = []
  let paragraph: Inline[][] | null = null

  const flushParagraph = () => {
    if (paragraph && paragraph.length > 0 && blocks.length < MAX_BLOCKS) {
      blocks.push({ t: 'p', lines: paragraph })
    }
    paragraph = null
  }

  for (const raw of lines) {
    if (blocks.length >= MAX_BLOCKS) break
    const line = raw.trimEnd()

    if (line.trim() === '') {
      flushParagraph()
      continue
    }

    const bullet = BULLET.exec(line.trimStart())
    const numbered = bullet ? null : NUMBERED.exec(line.trimStart())
    const marker = bullet ?? numbered
    if (marker) {
      const kind: ListBlock['t'] = bullet ? 'ul' : 'ol'
      flushParagraph()
      const previous = blocks[blocks.length - 1]
      let list: ListBlock
      if (previous !== undefined && previous.t === kind) {
        list = previous
      } else {
        // The flush above may have taken the last slot.
        if (blocks.length >= MAX_BLOCKS) continue
        list = { t: kind, items: [] }
        blocks.push(list)
      }
      if (list.items.length < MAX_LIST_ITEMS) list.items.push(parseInline(marker[1]))
      continue
    }

    paragraph ??= []
    if (paragraph.length < MAX_LINES_PER_PARAGRAPH) paragraph.push(parseInline(line))
  }

  flushParagraph()
  return blocks
}

// Links before emphasis so a `*` in link text stays literal. No nested quantifiers, so
// adversarial runs can't backtrack.
const INLINE = new RegExp(
  [
    '\\[([^\\]\\n]*)\\]\\(([^)\\s]+)\\)', // [text](url)
    '\\*\\*([^*\\n]+)\\*\\*', // **bold**
    '\\*([^*\\n]+)\\*', // *italic*
    '_([^_\\n]+)_', // _italic_
  ].join('|'),
  'g',
)

function parseInline(text: string): Inline[] {
  const out: Inline[] = []
  let cursor = 0

  INLINE.lastIndex = 0
  for (let m = INLINE.exec(text); m !== null; m = INLINE.exec(text)) {
    if (m.index > cursor) out.push({ t: 'text', v: text.slice(cursor, m.index) })
    cursor = m.index + m[0].length

    const [, linkText, linkHref, bold, star, underscore] = m
    if (linkHref !== undefined) {
      const href = safeLinkUrl(linkHref)
      // A rejected scheme degrades to the literal source text.
      out.push(href ? { t: 'link', href, v: linkText || href } : { t: 'text', v: m[0] })
    } else if (bold !== undefined) {
      out.push({ t: 'strong', v: bold })
    } else {
      out.push({ t: 'em', v: (star ?? underscore)! })
    }
  }

  if (cursor < text.length) out.push({ t: 'text', v: text.slice(cursor) })
  return out
}
