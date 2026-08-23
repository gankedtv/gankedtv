import { describe, it, expect } from 'vitest'
import { parseRichText, safeLinkUrl, type Block, type ListBlock } from '@/lib/richText'

function items(block: Block): string[] {
  return (block as ListBlock).items.map((item) =>
    item.map((node) => ('v' in node ? node.v : '')).join(''),
  )
}

describe('parseRichText', () => {
  it('returns nothing for empty input', () => {
    expect(parseRichText('')).toEqual([])
    expect(parseRichText(null)).toEqual([])
    expect(parseRichText(undefined)).toEqual([])
  })

  it('keeps single newlines as soft breaks inside one paragraph', () => {
    const [block] = parseRichText('first line\nsecond line')

    expect(block).toMatchObject({ t: 'p' })
    expect((block as { lines: unknown[] }).lines).toHaveLength(2)
  })

  it('splits paragraphs on a blank line', () => {
    const blocks = parseRichText('one\n\ntwo')

    expect(blocks.map((b) => b.t)).toEqual(['p', 'p'])
  })

  it('groups consecutive bullets into one list', () => {
    const blocks = parseRichText('Roles:\n- support\n- jungle\n* mid')

    expect(blocks.map((b) => b.t)).toEqual(['p', 'ul'])
    expect(items(blocks[1])).toEqual(['support', 'jungle', 'mid'])
  })

  it('groups consecutive numbers into an ordered list', () => {
    const blocks = parseRichText('1. first\n2. second\n3) third')

    expect(blocks.map((b) => b.t)).toEqual(['ol'])
    expect(items(blocks[0])).toEqual(['first', 'second', 'third'])
  })

  it('starts a new list when the marker kind changes', () => {
    const blocks = parseRichText('- bullet\n1. number')

    expect(blocks.map((b) => b.t)).toEqual(['ul', 'ol'])
  })

  it('needs whitespace after a marker so a hyphenated word is not a bullet', () => {
    const blocks = parseRichText('-not a list')

    expect(blocks.map((b) => b.t)).toEqual(['p'])
  })

  it('parses bold, italic and links', () => {
    const [block] = parseRichText('**loud** and *soft* and [home](https://ganked.tv)')
    const nodes = (block as { lines: { t: string; v: string }[][] }).lines[0]

    expect(nodes.filter((n) => n.t === 'strong').map((n) => n.v)).toEqual(['loud'])
    expect(nodes.filter((n) => n.t === 'em').map((n) => n.v)).toEqual(['soft'])
    expect(nodes.find((n) => n.t === 'link')).toMatchObject({
      href: 'https://ganked.tv',
      v: 'home',
    })
  })

  it('treats _underscores_ as italic', () => {
    const [block] = parseRichText('_soft_')
    const nodes = (block as { lines: { t: string; v: string }[][] }).lines[0]

    expect(nodes).toEqual([{ t: 'em', v: 'soft' }])
  })

  it('renders inline marks inside list items', () => {
    const blocks = parseRichText('- **main** role')
    const [item] = (blocks[0] as ListBlock).items

    expect(item[0]).toEqual({ t: 'strong', v: 'main' })
  })

  it('leaves unmatched markers as literal text', () => {
    const [block] = parseRichText('2 * 3 * 4 is not ** emphasis')
    const nodes = (block as { lines: { t: string }[][] }).lines[0]

    expect(nodes.every((n) => n.t === 'text' || n.t === 'em')).toBe(true)
  })

  it('never emits raw HTML — angle brackets stay literal text', () => {
    const [block] = parseRichText('<script>alert(1)</script>')
    const nodes = (block as { lines: { t: string; v: string }[][] }).lines[0]

    expect(nodes).toEqual([{ t: 'text', v: '<script>alert(1)</script>' }])
  })

  it.each([
    'javascript:alert(1)',
    'JavaScript:alert(1)',
    'data:text/html,<script>alert(1)</script>',
    'vbscript:msgbox(1)',
    'file:///etc/passwd',
    '/relative/path',
    'ganked.tv',
  ])('rejects %s as a link href and keeps the source text', (href) => {
    const source = `[click](${href})`
    const [block] = parseRichText(source)
    const nodes = (block as { lines: { t: string; v: string }[][] }).lines[0]

    // Hrefs containing `)` end up split across several text nodes — what matters is that no
    // link is produced and nothing is dropped.
    expect(nodes.every((n) => n.t === 'text')).toBe(true)
    expect(nodes.map((n) => n.v).join('')).toBe(source)
  })

  it('falls back to the href when the link text is empty', () => {
    const [block] = parseRichText('[](https://ganked.tv)')
    const nodes = (block as { lines: { t: string; v: string }[][] }).lines[0]

    expect(nodes).toEqual([{ t: 'link', href: 'https://ganked.tv', v: 'https://ganked.tv' }])
  })

  it('normalises CRLF', () => {
    const blocks = parseRichText('one\r\n\r\ntwo')

    expect(blocks.map((b) => b.t)).toEqual(['p', 'p'])
  })

  it('caps list items so a pathological bio cannot blow up the tree', () => {
    const blocks = parseRichText(Array.from({ length: 250 }, () => '- x').join('\n'))

    expect(blocks).toHaveLength(1)
    expect(items(blocks[0]).length).toBeLessThanOrEqual(40)
  })

  it('caps block count', () => {
    const blocks = parseRichText(Array.from({ length: 200 }, (_, i) => `p${i}`).join('\n\n'))

    expect(blocks.length).toBeLessThanOrEqual(60)
  })

  it.each([
    '*'.repeat(500),
    '['.repeat(500),
    '['.repeat(250) + ']'.repeat(250),
    '_'.repeat(500),
    '`'.repeat(500),
  ])('terminates promptly on adversarial input', (input) => {
    const started = performance.now()

    parseRichText(input)

    expect(performance.now() - started).toBeLessThan(250)
  })
})

describe('safeLinkUrl', () => {
  it('accepts http and https', () => {
    expect(safeLinkUrl('https://ganked.tv')).toBe('https://ganked.tv')
    expect(safeLinkUrl('http://ganked.tv')).toBe('http://ganked.tv')
  })

  it('rejects everything else', () => {
    expect(safeLinkUrl('javascript:alert(1)')).toBeNull()
    expect(safeLinkUrl('not a url')).toBeNull()
  })
})
