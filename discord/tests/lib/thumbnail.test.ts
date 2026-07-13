import { describe, expect, test } from 'bun:test';
import { downloadThumbnail, THUMBNAIL_FILENAME } from '../../src/lib/thumbnail.ts';

function fetchImpl(impl: (url: unknown, init?: RequestInit) => Promise<Response>): typeof fetch {
  return impl as unknown as typeof fetch;
}

describe('downloadThumbnail', () => {
  test('null url → null without fetching', async () => {
    const result = await downloadThumbnail(
      null,
      fetchImpl(async () => {
        throw new Error('must not be called');
      }),
    );

    expect(result).toBeNull();
  });

  test('ok response → bytes', async () => {
    const bytes = await downloadThumbnail(
      'https://minio.local/thumbs/x.jpg?sig=1',
      fetchImpl(async () => new Response(Buffer.from('JPEGDATA'))),
    );

    expect(bytes).not.toBeNull();
    expect(bytes!.toString()).toBe('JPEGDATA');
  });

  test('non-ok response (expired presign → 403) → null', async () => {
    const bytes = await downloadThumbnail(
      'https://minio.local/thumbs/x.jpg',
      fetchImpl(async () => new Response('denied', { status: 403 })),
    );

    expect(bytes).toBeNull();
  });

  test('network failure → null instead of throwing', async () => {
    const bytes = await downloadThumbnail(
      'http://localhost:9000/thumbs/x.jpg',
      fetchImpl(async () => {
        throw new Error('ECONNREFUSED');
      }),
    );

    expect(bytes).toBeNull();
  });

  test('empty body → null', async () => {
    const bytes = await downloadThumbnail(
      'https://minio.local/thumbs/x.jpg',
      fetchImpl(async () => new Response(Buffer.alloc(0))),
    );

    expect(bytes).toBeNull();
  });

  test('oversized body → null (Discord upload cap)', async () => {
    const bytes = await downloadThumbnail(
      'https://minio.local/thumbs/x.jpg',
      fetchImpl(async () => new Response(Buffer.alloc(8 * 1024 * 1024 + 1))),
    );

    expect(bytes).toBeNull();
  });

  test('oversized Content-Length → null before the body is buffered', async () => {
    const bytes = await downloadThumbnail(
      'https://minio.local/thumbs/x.jpg',
      fetchImpl(async () => {
        const resp = new Response('tiny');
        resp.headers.set('content-length', String(9 * 1024 * 1024));
        return resp;
      }),
    );

    expect(bytes).toBeNull();
  });

  test('wires an abort signal and maps a timeout abort to null', async () => {
    let seenSignal: unknown;
    const bytes = await downloadThumbnail(
      'https://minio.local/thumbs/x.jpg',
      fetchImpl(async (_url, init) => {
        seenSignal = init?.signal;
        throw new DOMException('The operation timed out.', 'TimeoutError');
      }),
    );

    expect(seenSignal).toBeInstanceOf(AbortSignal);
    expect(bytes).toBeNull();
  });

  test('attachment filename is a stable jpg name', () => {
    expect(THUMBNAIL_FILENAME).toBe('clip.jpg');
  });
});
