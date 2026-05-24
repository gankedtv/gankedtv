import { describe, expect, test } from 'bun:test';
import { shareUrl } from '../src/lib/shareUrl.ts';

describe('shareUrl', () => {
  test('builds the canonical /c/{code} URL', () => {
    expect(shareUrl('abc12345', 'https://gankedtv.com')).toBe('https://gankedtv.com/c/abc12345');
  });

  test('strips a trailing slash from the base', () => {
    expect(shareUrl('abc12345', 'https://gankedtv.com/')).toBe('https://gankedtv.com/c/abc12345');
  });

  test('supports custom dev hosts', () => {
    expect(shareUrl('xyz', 'http://localhost:5173')).toBe('http://localhost:5173/c/xyz');
  });
});
