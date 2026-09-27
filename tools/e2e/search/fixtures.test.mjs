import assert from 'node:assert/strict';
import { mkdtemp, open, readdir, readlink, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { createConnection } from 'node:net';

import { createFixtureServer, createSearchBarrier } from './fixtures.mjs';

const releases = [
  {
    guid: 'alpha',
    title: 'League Alpha Final 2026 1080p',
    size: 101,
    publishDate: '2026-09-06T10:00:00Z',
    category: 5060,
    downloadPath: 'alpha.nzb',
    seeders: 0,
    infoHash: 'AAA',
    sportarrId: 'event-1',
  },
  {
    guid: 'beta',
    title: 'Final League Alpha Highlights 2026',
    size: 202,
    publishDate: '2026-09-05T10:00:00Z',
    category: 5000,
    downloadPath: 'beta.torrent',
    seeders: 7,
    infoHash: 'BBB',
    sportarrId: 'event-2',
  },
  {
    guid: 'gamma',
    title: 'League Alpha Final 2025',
    size: 303,
    publishDate: '2025-09-06T10:00:00Z',
    category: 5060,
    downloadPath: 'videos/gamma.mp4',
    seeders: null,
    infoHash: null,
    sportarrId: 'event-3',
  },
];

async function withServer(config, fn) {
  const server = createFixtureServer(config);
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  const { port } = server.address();
  try {
    await fn(`http://127.0.0.1:${port}`);
  } finally {
    await new Promise((resolve, reject) => server.close((error) => error ? reject(error) : resolve()));
  }
}

async function getItems(url) {
  const response = await fetch(url);
  const body = await response.text();
  return { response, body, guids: [...body.matchAll(/<guid[^>]*>([^<]+)<\/guid>/g)].map((match) => match[1]) };
}

test('search barrier holds a search while control and caps requests remain available', async () => {
  const barrier = createSearchBarrier({ timeoutMs: 1000 });
  const server = createFixtureServer({ releases }, { beforeSearchResponse: barrier.wait });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  try {
    const pendingSearch = fetch(`${base}/api?t=search&q=alpha`);
    await barrier.entered;
    assert.equal(barrier.pending, true);
    assert.equal((await fetch(`${base}/_fixture/requests`)).status, 200);
    assert.equal((await fetch(`${base}/api?t=caps`)).status, 200);
    assert.equal(barrier.pending, true);
    barrier.release();
    assert.equal((await pendingSearch).status, 200);
    assert.equal(barrier.pending, false);
  } finally {
    barrier.release();
    await new Promise((resolve, reject) => server.close((error) => error ? reject(error) : resolve()));
  }
});

test('search barrier timeout fails the held response and clears pending state', async () => {
  const barrier = createSearchBarrier({ timeoutMs: 10 });
  const server = createFixtureServer({ releases }, { beforeSearchResponse: barrier.wait });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  try {
    const responsePromise = fetch(`http://127.0.0.1:${server.address().port}/api?t=search`);
    await barrier.entered;
    assert.equal((await responsePromise).status, 500);
    assert.equal(barrier.pending, false);
    const ledger = await (await fetch(`http://127.0.0.1:${server.address().port}/_fixture/requests`)).json();
    assert.equal(ledger[0].status, 500);
    barrier.release();
  } finally {
    await new Promise((resolve, reject) => server.close((error) => error ? reject(error) : resolve()));
  }
  assert.throws(() => createSearchBarrier({ timeoutMs: 10_001 }), /timeoutMs/);
});

async function rawRequest(base, request) {
  const { hostname, port } = new URL(base);
  return new Promise((resolve, reject) => {
    const socket = createConnection({ host: hostname, port: Number(port) });
    let response = '';
    socket.setEncoding('utf8');
    socket.setTimeout(1000, () => socket.destroy(new Error('response timed out')));
    socket.once('error', reject);
    socket.on('data', (chunk) => { response += chunk; });
    socket.once('end', () => resolve(response));
    socket.once('connect', () => socket.end(request));
  });
}

test('AND query, dates, categories, and exact IDs control returned releases', async () => {
  await withServer({ releases, pageSize: 10 }, async (base) => {
    assert.deepEqual((await getItems(`${base}/api?t=search&q=alpha%20league`)).guids, ['alpha', 'beta', 'gamma']);
    assert.deepEqual((await getItems(`${base}/api?t=search&q=alpha%20missing`)).guids, []);
    assert.deepEqual((await getItems(`${base}/api?t=search&q=alpha&after=2026-01-01&before=2026-12-31`)).guids, ['alpha', 'beta']);
    assert.deepEqual((await getItems(`${base}/api?t=search&q=alpha&cat=5060`)).guids, ['alpha', 'gamma']);
    assert.deepEqual((await getItems(`${base}/api?t=search&sportarrid=event-2&q=does-not-match`)).guids, ['beta']);
  });
});

test('phrase and ordered modes preserve their configured query semantics', async () => {
  await withServer({ releases, searchMode: 'phrase' }, async (base) => {
    assert.deepEqual((await getItems(`${base}/api?t=search&q=league%20alpha%20final`)).guids, ['alpha', 'gamma']);
    assert.deepEqual((await getItems(`${base}/api?t=search&q=alpha%20final`)).guids, ['alpha', 'gamma']);
  });
  await withServer({ releases, searchMode: 'ordered' }, async (base) => {
    assert.deepEqual((await getItems(`${base}/api?t=search&q=league%20final`)).guids, ['alpha', 'gamma']);
    assert.deepEqual((await getItems(`${base}/api?t=search&q=final%20league`)).guids, ['beta']);
  });
});

test('phrase mode matches complete token phrases instead of word prefixes', async () => {
  const phraseReleases = [
    { ...releases[0], guid: 'cup', title: 'World Cup Final' },
    { ...releases[1], guid: 'cupcake', title: 'World Cupcake Final' },
  ];
  await withServer({ releases: phraseReleases, searchMode: 'phrase' }, async (base) => {
    assert.deepEqual((await getItems(`${base}/api?t=search&q=cup`)).guids, ['cup']);
  });
});

test('null searchMode uses AND matching', async () => {
  await withServer({ releases, searchMode: null }, async (base) => {
    assert.deepEqual((await getItems(`${base}/api?t=search&q=alpha%20league`)).guids, ['alpha', 'beta', 'gamma']);
  });
});

test('search and RSS paginate in newest-first order and expose Newznab metadata', async () => {
  await withServer({ releases, pageSize: 1 }, async (base) => {
    const first = await getItems(`${base}/api?t=search&q=alpha&limit=20&offset=0`);
    const second = await getItems(`${base}/api?t=search&q=alpha&limit=20&offset=1`);
    const rss = await getItems(`${base}/api?t=search&offset=2`);
    assert.deepEqual(first.guids, ['alpha']);
    assert.deepEqual(second.guids, ['beta']);
    assert.deepEqual(rss.guids, ['gamma']);
    assert.match(first.body, /<newznab:response offset="0" total="3"/);
    assert.match(second.body, /name="seeders" value="7"/);
  });
});

test('Torznab source paths expose Torznab attributes', async () => {
  await withServer({ releases }, async (base) => {
    const result = await getItems(`${base}/sources/one/torznab?t=search&q=alpha`);
    assert.equal(result.response.status, 200);
    assert.match(result.body, /xmlns:torznab=/);
    assert.match(result.body, /<torznab:attr name="infohash" value="AAA"/);
  });
});

test('caps, request reset, deterministic source errors, and quotas are observable', async () => {
  await withServer({ releases, quota: 2, sourceErrors: [{ attempt: 1, status: 503, body: 'planned outage' }] }, async (base) => {
    const caps = await fetch(`${base}/api?t=caps`);
    assert.equal(caps.status, 503);
    assert.equal(await caps.text(), 'planned outage');
    assert.equal((await fetch(`${base}/api?t=caps`)).status, 200);
    assert.equal((await fetch(`${base}/api?t=search&q=alpha`)).status, 429);

    const ledgerResponse = await fetch(`${base}/_fixture/requests`);
    const ledger = await ledgerResponse.json();
    assert.deepEqual(ledger.map((entry) => entry.status), [503, 200, 429]);
    assert.deepEqual(ledger.map((entry) => entry.attempt), [1, 2, 3]);

    assert.equal((await fetch(`${base}/_fixture/reset`, { method: 'POST' })).status, 204);
    assert.deepEqual(await (await fetch(`${base}/_fixture/requests`)).json(), []);
    assert.equal((await fetch(`${base}/api?t=caps`)).status, 503);
  });
});

test('payload serving stays inside payloadRoot and rejects traversal', async () => {
  const payloadRoot = await mkdtemp(join(tmpdir(), 'sportarr-fixtures-'));
  await writeFile(join(payloadRoot, 'alpha.nzb'), 'payload-alpha');
  await withServer({ releases, payloadRoot }, async (base) => {
    const payload = await fetch(`${base}/payload/alpha.nzb`);
    assert.equal(payload.status, 200);
    assert.equal(await payload.text(), 'payload-alpha');
    const rawTraversal = await rawRequest(base, 'GET /payload/../fixtures.mjs HTTP/1.1\r\nHost: fixture\r\nConnection: close\r\n\r\n');
    assert.match(rawTraversal, /^HTTP\/1\.1 400 /);
    assert.equal((await fetch(`${base}/payload/%2e%2e%2ffixtures.mjs`)).status, 400);
    assert.equal((await fetch(`${base}/payload/not-listed.nzb`)).status, 404);
  });
});

test('malformed Host headers return an error and leave the server available', async () => {
  await withServer({ releases }, async (base) => {
    const malformed = await rawRequest(base, 'GET /api?t=caps HTTP/1.1\r\nHost: [\r\nConnection: close\r\n\r\n');
    assert.match(malformed, /^HTTP\/1\.1 500 /);
    assert.equal((await fetch(`${base}/api?t=caps`)).status, 200);
  });
});

test('payload serving rejects an allowlisted symlink outside payloadRoot', async () => {
  const payloadRoot = await mkdtemp(join(tmpdir(), 'sportarr-fixtures-'));
  const outsideRoot = await mkdtemp(join(tmpdir(), 'sportarr-outside-'));
  const outsideFile = join(outsideRoot, 'outside.nzb');
  await writeFile(outsideFile, 'outside payload');
  await symlink(outsideFile, join(payloadRoot, 'linked.nzb'));
  await withServer({ releases: [{ ...releases[0], downloadPath: 'linked.nzb' }], payloadRoot }, async (base) => {
    assert.equal((await fetch(`${base}/payload/linked.nzb`)).status, 400);
    assert.equal((await fetch(`${base}/api?t=caps`)).status, 200);
  });
});

test('explicit payloadFiles serve media used by torrent webseeds', async () => {
  const payloadRoot = await mkdtemp(join(tmpdir(), 'sportarr-fixtures-'));
  await writeFile(join(payloadRoot, 'alpha.torrent'), 'torrent metadata');
  await writeFile(join(payloadRoot, 'alpha-video.mkv'), 'webseed media');
  await withServer({
    releases: [{ ...releases[0], downloadPath: 'alpha.torrent' }],
    payloadFiles: ['alpha-video.mkv'],
    payloadRoot,
  }, async (base) => {
    const media = await fetch(`${base}/payload/alpha-video.mkv`);
    assert.equal(media.status, 200);
    assert.equal(await media.text(), 'webseed media');
    assert.equal((await fetch(`${base}/payload/unlisted.mkv`)).status, 404);
  });
});

test('payload stream failures close the response without crashing the server', async () => {
  const payloadRoot = await mkdtemp(join(tmpdir(), 'sportarr-fixtures-'));
  await writeFile(join(payloadRoot, 'broken.nzb'), 'content that must not complete');
  await withServer({
    releases: [{ ...releases[0], downloadPath: 'broken.nzb' }],
    payloadRoot,
    payloadStreamErrors: ['broken.nzb'],
  }, async (base) => {
    await assert.rejects(fetch(`${base}/payload/broken.nzb`).then((response) => response.arrayBuffer()));
    assert.equal((await fetch(`${base}/api?t=caps`)).status, 200);
  });
});

test('payloads support HEAD and byte ranges for webseed clients', async () => {
  const payloadRoot = await mkdtemp(join(tmpdir(), 'sportarr-fixtures-'));
  await writeFile(join(payloadRoot, 'video.mkv'), '0123456789');
  await withServer({ releases: [], payloadFiles: ['video.mkv'], payloadRoot }, async (base) => {
    const head = await fetch(`${base}/payload/video.mkv`, { method: 'HEAD' });
    assert.equal(head.status, 200);
    assert.equal(head.headers.get('content-length'), '10');
    assert.equal(head.headers.get('accept-ranges'), 'bytes');
    assert.equal(await head.text(), '');

    const range = await fetch(`${base}/payload/video.mkv`, { headers: { range: 'bytes=2-5' } });
    assert.equal(range.status, 206);
    assert.equal(range.headers.get('content-range'), 'bytes 2-5/10');
    assert.equal(range.headers.get('content-length'), '4');
    assert.equal(await range.text(), '2345');

    const suffix = await fetch(`${base}/payload/video.mkv`, { headers: { range: 'bytes=-3' } });
    assert.equal(suffix.status, 206);
    assert.equal(await suffix.text(), '789');

    const invalid = await fetch(`${base}/payload/video.mkv`, { headers: { range: 'bytes=20-30' } });
    assert.equal(invalid.status, 416);
    assert.equal(invalid.headers.get('content-range'), 'bytes */10');
  });
});

test('payload retrieval has a separate ledger from indexer query budgets', async () => {
  const payloadRoot = await mkdtemp(join(tmpdir(), 'sportarr-payload-ledger-'));
  await writeFile(join(payloadRoot, 'alpha.nzb'), 'actual fixture bytes');
  await withServer({ releases: [releases[0]], payloadRoot }, async base => {
    assert.equal(await (await fetch(`${base}/payload/alpha.nzb`)).text(), 'actual fixture bytes');
    const ledger = await (await fetch(`${base}/_fixture/payload-requests`)).json();
    assert.equal(ledger.length, 1);
    assert.equal(ledger[0].method, 'GET');
    assert.equal(ledger[0].status, 200);
    assert.equal(ledger[0].declaredBytes, 20);
    assert.equal(ledger[0].finished, true);
    assert.deepEqual(await (await fetch(`${base}/_fixture/requests`)).json(), []);
    await fetch(`${base}/_fixture/reset`, { method: 'POST' });
    assert.deepEqual(await (await fetch(`${base}/_fixture/payload-requests`)).json(), []);
  });
});

test('aborted payload retrieval closes its source file descriptor', async () => {
  const payloadRoot = await mkdtemp(join(tmpdir(), 'sportarr-payload-abort-'));
  const payloadPath = join(payloadRoot, 'large.mkv');
  const handle = await open(payloadPath, 'w');
  await handle.truncate(128 * 1024 * 1024);
  await handle.close();
  async function openPayloadDescriptors() {
    const descriptors = await readdir('/proc/self/fd');
    const targets = await Promise.all(descriptors.map((descriptor) => readlink(`/proc/self/fd/${descriptor}`).catch(() => '')));
    return targets.filter((target) => target === payloadPath).length;
  }
  try {
    await withServer({ releases: [], payloadFiles: ['large.mkv'], payloadRoot }, async (base) => {
      const controller = new AbortController();
      const response = await fetch(`${base}/payload/large.mkv`, { signal: controller.signal });
      await response.body.getReader().read();
      controller.abort();
      let descriptors = await openPayloadDescriptors();
      for (let attempt = 0; descriptors !== 0 && attempt < 20; attempt += 1) {
        await new Promise((resolve) => setImmediate(resolve));
        descriptors = await openPayloadDescriptors();
      }
      assert.equal(descriptors, 0);
    });
  } finally {
    await rm(payloadRoot, { recursive: true, force: true });
  }
});
