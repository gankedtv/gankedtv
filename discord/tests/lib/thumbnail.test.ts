import { afterEach, describe, expect, test } from 'bun:test';
import { fetchThumbnail, THUMBNAIL_FILENAME } from '../../src/lib/thumbnail.ts';

const realFetch = globalThis.fetch;

afterEach(() => {
  globalThis.fetch = realFetch;
});

function stubFetch(impl: () => Promise<Response>) {
  globalThis.fetch = impl as unknown as typeof fetch;
}

describe('fetchThumbnail', () => {
  test('null url → null without fetching', async () => {
    stubFetch(async () => {
      throw new Error('must not be called');
    });

    expect(await fetchThumbnail(null)).toBeNull();
  });

  test('ok response → bytes', async () => {
    stubFetch(async () => new Response(Buffer.from('JPEGDATA')));

    const bytes = await fetchThumbnail('https://minio.local/thumbs/x.jpg?sig=1');

    expect(bytes).not.toBeNull();
    expect(bytes!.toString()).toBe('JPEGDATA');
  });

  test('non-ok response (expired presign → 403) → null', async () => {
    stubFetch(async () => new Response('denied', { status: 403 }));

    expect(await fetchThumbnail('https://minio.local/thumbs/x.jpg')).toBeNull();
  });

  test('network failure → null instead of throwing', async () => {
    stubFetch(async () => {
      throw new Error('ECONNREFUSED');
    });

    expect(await fetchThumbnail('http://localhost:9000/thumbs/x.jpg')).toBeNull();
  });

  test('empty body → null', async () => {
    stubFetch(async () => new Response(Buffer.alloc(0)));

    expect(await fetchThumbnail('https://minio.local/thumbs/x.jpg')).toBeNull();
  });

  test('oversized body → null (Discord upload cap)', async () => {
    stubFetch(async () => new Response(Buffer.alloc(8 * 1024 * 1024 + 1)));

    expect(await fetchThumbnail('https://minio.local/thumbs/x.jpg')).toBeNull();
  });

  test('attachment filename is a stable jpg name', () => {
    expect(THUMBNAIL_FILENAME).toBe('clip.jpg');
  });
});
