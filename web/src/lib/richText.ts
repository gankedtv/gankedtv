/**
 * A deliberately small markdown subset for user-authored prose (today: the profile bio).
 *
 * The parser returns a typed tree, never an HTML string, so `RichText.vue` can render it with
 * real Vue elements. That keeps the codebase's "no `v-html` anywhere" invariant intact — with
 * no HTML ever generated there is no injection channel to sanitise, and no sanitiser bypass to
 * keep patched. It also means no new runtime dependency for a 500-character field.
 *
 * Supported: paragraphs (blank line breaks), soft line breaks, `- `/`* ` bullets, `1. ` numbers,
 * `**bold**`, `*italic*`/`_italic_`, and `[text](url)` links restricted to http/https.
 * Everything else — raw HTML, headings, images, code spans, tables, nested lists — is left as
 * literal text. Code spans are deliberately out: a monospace face would be a third font, which
 * the design system doesn't have.
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

// Hard caps. The bio is capped at 500 characters server-side, so these are only ever hit by
// pathological input (500 lone hyphens is 250 list items); they bound the render tree rather
// than reject anything a person would plausibly write.
const MAX_BLOCKS = 60
const MAX_LIST_ITEMS = 40
const MAX_LINES_PER_PARAGRAPH = 40

const BULLET = /^[-*]\s+(.*)$/
const NUMBERED = /^\d{1,3}[.)]\s+(.*)$/

/**
 * Only http/https survive. `javascript:` and `data:` are the reason this exists; a bare
 * `example.com` is not linkified at all, so a scheme-less href can't resolve against the app's
 * own origin and impersonate an internal route.
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
    if (paragraph && paragraph.length > 0) blocks.push({ t: 'p', lines: paragraph })
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

// Links before emphasis so a `*` inside link text stays literal. Each alternative is
// non-greedy with no nested quantifier, so there is no backtracking blowup on adversarial
// runs of `*` or `[`.
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
