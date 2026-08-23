/**
 * A small markdown subset for the profile bio: paragraphs, soft line breaks, bullets, numbering,
 * bold, italic, and http(s) links. Everything else stays literal text.
 *
 * Returns a typed tree, never an HTML string, so `RichText.vue` builds real elements — that is
 * what keeps the repo's "no `v-html` anywhere" invariant, and with no markup generated there is
 * nothing to sanitise. Code spans are out: a monospace face would be a third font.
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

// Bound the render tree against pathological input (500 lone hyphens is 250 list items). The
// bio is 500 chars server-side, so nothing a person would write comes near these.
const MAX_BLOCKS = 60
const MAX_LIST_ITEMS = 40
const MAX_LINES_PER_PARAGRAPH = 40

const BULLET = /^[-*]\s+(.*)$/
const NUMBERED = /^\d{1,3}[.)]\s+(.*)$/

/**
 * Only http/https survive — `javascript:` and `data:` are the point. A scheme-less href is
 * rejected too, so `example.com` can't resolve against our own origin and fake an internal route.
 */
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
      // Consecutive markers of the same kind extend the open list; switching kind starts a
      // new one, so a bullet list followed by a numbered list renders as two lists.
      const previous = blocks[blocks.length - 1]
      let list: ListBlock
      if (previous !== undefined && previous.t === kind) {
        list = previous
      } else {
        // The flush above may have just consumed the last slot; an open list of the same kind
        // can still grow, but a new one would push the tree over the cap.
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

// Links before emphasis so a `*` inside link text stays literal. No nested quantifiers, so
// adversarial runs of `*` or `[` can't cause backtracking blowup.
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
      // A rejected scheme degrades to the literal source text rather than a bare, clickable-
      // looking span — the reader sees exactly what was written.
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
