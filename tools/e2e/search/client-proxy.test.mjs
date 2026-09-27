import assert from 'node:assert/strict';
import http from 'node:http';
import { afterEach, test } from 'node:test';

import { createClientProxy, createClientProxyForTest } from './client-proxy.mjs';

const servers = new Set();

afterEach(async () => {
  await Promise.all([...servers].map((server) => new Promise((resolve) => {
    server.closeAllConnections?.();
    server.close(resolve);
  })));
  servers.clear();
});

async function listen(server) {
  servers.add(server);
  await new Promise((resolve, reject) => server.listen(0, '127.0.0.1', resolve).once('error', reject));
  return `http://127.0.0.1:${server.address().port}`;
}

async function request(url, { method = 'GET', path = '/', headers = {}, body } = {}) {
  return new Promise((resolve, reject) => {
    const target = new URL(path, url);
    const req = http.request(target, { method, headers }, (res) => {
      const chunks = [];
      res.on('data', (chunk) => chunks.push(chunk));
      res.on('end', () => resolve({ status: res.statusCode, body: Buffer.concat(chunks), headers: res.headers }));
    });
    req.once('error', reject);
    if (body !== undefined) req.end(body);
    else req.end();
  });
}

function upstream(handler) {
  return http.createServer(handler);
}

test('createClientProxy permits only its fixed client destinations', () => {
  const qbit = createClientProxy({ clientType: 'qbittorrent' });
  const sab = createClientProxy({ clientType: 'sabnzbd' });
  servers.add(qbit);
  servers.add(sab);
  assert.equal(qbit.target.origin, 'http://sv-b0-qbit:8080');
  assert.equal(sab.target.origin, 'http://sv-b0-sab:8080');
  assert.throws(() => createClientProxy({ clientType: 'other' }), /clientType/);
  assert.throws(() => createClientProxy({ clientType: 'sabnzbd', target: 'http://example.com' }), /target/);
  assert.throws(() => createClientProxyForTest({ clientType: 'sabnzbd', target: 'http://example.com' }), /loopback/);
  assert.throws(() => createClientProxyForTest({ clientType: 'sabnzbd', target: 'https://127.0.0.1:8080' }), /HTTP/);
});

test('qBittorrent forwards raw requests and records each accepted add', async () => {
  const seen = [];
  const target = await listen(upstream((req, res) => {
    const chunks = [];
    req.on('data', (chunk) => chunks.push(chunk));
    req.on('end', () => {
      seen.push({ method: req.method, url: req.url, body: Buffer.concat(chunks).toString() });
      res.writeHead(200, { 'content-type': 'text/plain', location: 'http://evil.invalid/' });
      res.end('Ok.');
    });
  }));
  const proxy = createClientProxyForTest({ clientType: 'qbittorrent', target });
  const proxyUrl = await listen(proxy);
  const body = 'urls=magnet%3Afixture&category=test';
  const first = await request(proxyUrl, { method: 'POST', path: '/api/v2/torrents/add?token=secret', body });
  const second = await request(proxyUrl, { method: 'POST', path: '/api/v2/torrents/add?token=secret', body });
  assert.equal(first.body.toString(), 'Ok.');
  assert.equal(second.body.toString(), 'Ok.');
  assert.deepEqual(seen, [
    { method: 'POST', url: '/api/v2/torrents/add?token=secret', body },
    { method: 'POST', url: '/api/v2/torrents/add?token=secret', body },
  ]);
  assert.equal(proxy.requests.length, 2);
  assert.ok(proxy.requests.every((entry) => entry.kind === 'add' && entry.accepted === true && entry.responseStatus === 200));
  assert.ok(proxy.requests.every((entry) => entry.path === '/api/v2/torrents/add' && entry.elapsedMs >= 0));
  assert.doesNotMatch(JSON.stringify(proxy.requests), /secret|magnet|token/);
});

test('qBittorrent records failed add responses', async () => {
  const target = await listen(upstream((req, res) => res.end('Fails.')));
  const proxy = createClientProxyForTest({ clientType: 'qbittorrent', target });
  const proxyUrl = await listen(proxy);
  await request(proxyUrl, { method: 'POST', path: '/api/v2/torrents/add' });
  assert.equal(proxy.requests[0].accepted, false);
});

test('SAB detects query and body add modes and returns actual job IDs', async () => {
  let sequence = 0;
  const target = await listen(upstream((req, res) => {
    req.resume();
    req.on('end', () => {
      sequence += 1;
      res.setHeader('content-type', 'application/json');
      res.end(JSON.stringify({ status: true, nzo_ids: [`SABnzbd_nzo_${sequence}`] }));
    });
  }));
  const proxy = createClientProxyForTest({ clientType: 'sabnzbd', target });
  const proxyUrl = await listen(proxy);
  await request(proxyUrl, { method: 'GET', path: '/api?mode=addurl&name=http%3A%2F%2Fsecret.invalid&apikey=hidden' });
  await request(proxyUrl, {
    method: 'POST', path: '/api?apikey=hidden', headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: 'mode=addfile&name=private-nzb-data',
  });
  assert.deepEqual(proxy.requests.map(({ method, path, kind, accepted, returnedJobIds }) => ({ method, path, kind, accepted, returnedJobIds })), [
    { method: 'GET', path: '/api', kind: 'add', accepted: true, returnedJobIds: ['SABnzbd_nzo_1'] },
    { method: 'POST', path: '/api', kind: 'add', accepted: true, returnedJobIds: ['SABnzbd_nzo_2'] },
  ]);
  assert.doesNotMatch(JSON.stringify(proxy.requests), /hidden|private|secret|apikey/);
});

test('SAB classifies a body-mode add when upstream responds before upload ends', async () => {
  let markUpstreamFinished;
  const upstreamFinished = new Promise((resolve) => { markUpstreamFinished = resolve; });
  const target = await listen(upstream((req, res) => {
    req.once('data', () => {
      res.once('finish', markUpstreamFinished);
      res.end(JSON.stringify({ status: true, nzo_ids: ['SABnzbd_nzo_early'] }));
    });
  }));
  const proxy = createClientProxyForTest({ clientType: 'sabnzbd', target });
  const proxyUrl = await listen(proxy);
  let finishDownstream;
  const downstreamFinished = new Promise((resolve) => { finishDownstream = resolve; });
  const req = http.request(`${proxyUrl}/api`, {
      method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' },
  }, (res) => {
    res.resume();
    res.once('end', finishDownstream);
  });
  const requestError = new Promise((_, reject) => req.once('error', reject));
  req.write('mode=addfile&name=');
  await Promise.race([upstreamFinished, requestError]);
  assert.equal(proxy.requests[0].accepted, false);
  req.end('private-nzb-data');
  await Promise.race([downstreamFinished, requestError]);
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(proxy.requests[0].kind, 'add');
  assert.equal(proxy.requests[0].accepted, true);
  assert.deepEqual(proxy.requests[0].returnedJobIds, ['SABnzbd_nzo_early']);
});

test('SAB requires status true and a returned job ID for acceptance', async () => {
  const target = await listen(upstream((req, res) => {
    req.resume();
    res.end(JSON.stringify({ status: false, nzo_ids: [] }));
  }));
  const proxy = createClientProxyForTest({ clientType: 'sabnzbd', target });
  const proxyUrl = await listen(proxy);
  await request(proxyUrl, { path: '/api?mode=addurl&apikey=hidden' });
  assert.deepEqual(proxy.requests[0].returnedJobIds, []);
  assert.equal(proxy.requests[0].accepted, false);
});

test('redirect responses are returned without following their destination', async () => {
  let redirected = 0;
  const destination = await listen(upstream((req, res) => { redirected += 1; res.end('wrong'); }));
  const target = await listen(upstream((req, res) => {
    res.writeHead(302, { location: `${destination}/capture?key=secret` });
    res.end();
  }));
  const proxy = createClientProxyForTest({ clientType: 'qbittorrent', target });
  const result = await request(await listen(proxy), { method: 'POST', path: '/api/v2/torrents/add' });
  assert.equal(result.status, 302);
  assert.equal(redirected, 0);
  assert.equal(proxy.requests[0].accepted, false);
});

test('upstream disconnects fail closed and do not terminate the proxy', async () => {
  let first = true;
  const target = await listen(upstream((req, res) => {
    if (first) {
      first = false;
      req.socket.destroy();
      return;
    }
    res.end('Ok.');
  }));
  const proxy = createClientProxyForTest({ clientType: 'qbittorrent', target });
  const proxyUrl = await listen(proxy);
  const failed = await request(proxyUrl, { method: 'POST', path: '/api/v2/torrents/add' });
  const recovered = await request(proxyUrl, { method: 'POST', path: '/api/v2/torrents/add' });
  assert.equal(failed.status, 502);
  assert.equal(proxy.requests[0].accepted, false);
  assert.equal(recovered.body.toString(), 'Ok.');
  assert.equal(proxy.requests[1].accepted, true);
});

test('mid-response upstream resets close the caller and proxy remains available', async () => {
  let first = true;
  const target = await listen(upstream((req, res) => {
    if (first) {
      first = false;
      res.writeHead(200, { 'content-type': 'text/plain' });
      res.flushHeaders();
      res.write('partial');
      setTimeout(() => res.socket.destroy(), 5);
      return;
    }
    res.end('Ok.');
  }));
  const proxy = createClientProxyForTest({ clientType: 'qbittorrent', target });
  const proxyUrl = await listen(proxy);
  await new Promise((resolve, reject) => {
    const req = http.get(`${proxyUrl}/partial`, (res) => {
      assert.equal(res.statusCode, 200);
      res.once('aborted', resolve);
      res.once('error', (error) => error.code === 'ECONNRESET' ? resolve() : reject(error));
      res.once('end', () => reject(new Error('truncated upstream response ended normally')));
      res.resume();
    });
    req.once('error', (error) => error.code === 'ECONNRESET' ? resolve() : reject(error));
  });
  const recovered = await request(proxyUrl, { method: 'POST', path: '/api/v2/torrents/add' });
  assert.equal(recovered.body.toString(), 'Ok.');
  assert.equal(proxy.requests[0].accepted, false);
  assert.equal(proxy.requests[1].accepted, true);
});

test('absolute timeout stops a continuously trickling upstream response', async () => {
  const target = await listen(upstream((req, res) => {
    res.writeHead(200);
    const timer = setInterval(() => res.write('.'), 5);
    res.once('close', () => clearInterval(timer));
  }));
  const proxy = createClientProxyForTest({ clientType: 'qbittorrent', target, timeoutMs: 30 });
  const proxyUrl = await listen(proxy);
  const startedAt = Date.now();
  await new Promise((resolve, reject) => {
    const req = http.get(`${proxyUrl}/slow`, (res) => {
      res.once('aborted', resolve);
      res.once('error', (error) => error.code === 'ECONNRESET' ? resolve() : reject(error));
      res.resume();
    });
    req.once('error', (error) => error.code === 'ECONNRESET' ? resolve() : reject(error));
  });
  assert.ok(Date.now() - startedAt < 500);
  assert.equal(proxy.requests[0].accepted, false);
});

test('timed out SAB body add remains visible as a failed add', async () => {
  const target = await listen(upstream((req) => req.resume()));
  const proxy = createClientProxyForTest({ clientType: 'sabnzbd', target, timeoutMs: 30 });
  const result = await request(await listen(proxy), {
    method: 'POST', path: '/api', headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: 'mode=addfile&name=private-nzb-data',
  });
  assert.equal(result.status, 504);
  assert.equal(proxy.requests[0].kind, 'add');
  assert.equal(proxy.requests[0].accepted, false);
});

test('proxy removes hop-by-hop request and response headers', async () => {
  let receivedHeaders;
  const target = await listen(upstream((req, res) => {
    receivedHeaders = req.headers;
    res.writeHead(200, { connection: 'x-private', 'x-private': 'hidden', 'x-end-to-end': 'kept' });
    res.end('Ok.');
  }));
  const proxy = createClientProxyForTest({ clientType: 'qbittorrent', target });
  const result = await request(await listen(proxy), {
    method: 'POST', path: '/api/v2/torrents/add', headers: { connection: 'x-secret', 'x-secret': 'hidden' },
  });
  assert.equal(receivedHeaders['x-secret'], undefined);
  assert.equal(result.headers['x-private'], undefined);
  assert.equal(result.headers['x-end-to-end'], 'kept');
});

test('client disconnects do not terminate the proxy', async () => {
  const target = await listen(upstream((req, res) => {
    if (req.url === '/slow') {
      res.write(Buffer.alloc(128 * 1024, 0x61));
      setTimeout(() => res.end('done'), 20);
      return;
    }
    res.end('Ok.');
  }));
  const proxy = createClientProxyForTest({ clientType: 'qbittorrent', target });
  const proxyUrl = await listen(proxy);
  await new Promise((resolve, reject) => {
    const req = http.get(`${proxyUrl}/slow`, (res) => {
      res.once('data', () => res.destroy());
      res.once('close', resolve);
    });
    req.once('error', reject);
  });
  const recovered = await request(proxyUrl, { method: 'POST', path: '/api/v2/torrents/add' });
  assert.equal(recovered.body.toString(), 'Ok.');
  assert.equal(proxy.requests[1].accepted, true);
});

test('large non-add responses stream without entering the inspection ledger', async () => {
  const payload = Buffer.alloc(256 * 1024, 0x61);
  const target = await listen(upstream((req, res) => res.end(payload)));
  const proxy = createClientProxyForTest({ clientType: 'qbittorrent', target });
  const result = await request(await listen(proxy), { path: '/api/v2/app/version' });
  assert.equal(result.body.length, payload.length);
  assert.equal(proxy.requests[0].kind, 'other');
  assert.equal(proxy.requests[0].accepted, false);
});

test('SAB recognizes quoted and unquoted .NET multipart control fields', async () => {
  const target = await listen(upstream((req, res) => { req.resume(); req.on('end', () => res.end(JSON.stringify({ status: true, nzo_ids: ['one'] }))); }));
  const proxy = createClientProxyForTest({ clientType: 'sabnzbd', target });
  const proxyUrl = await listen(proxy);
  for (const name of ['mode', '"mode"']) {
    await request(proxyUrl, { method: 'POST', path: '/api', headers: { 'content-type': 'multipart/form-data; boundary=fixture' },
      body: `--fixture\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Disposition: form-data; name=${name}\r\n\r\naddfile\r\n--fixture--\r\n` });
  }
  assert.deepEqual(proxy.requests.map(row => [row.kind, row.accepted]), [['add', true], ['add', true]]);
});

test('qBittorrent records the structured add result from the current API', async () => {
  let result = { added_torrent_ids: ['a'.repeat(40)], failure_count: 0, pending_count: 0, success_count: 1 };
  const target = await listen(upstream((req, res) => { req.resume(); req.on('end', () => res.end(JSON.stringify(result))); }));
  const proxy = createClientProxyForTest({ clientType: 'qbittorrent', target });
  const url = await listen(proxy);
  await request(url, { method: 'POST', path: '/api/v2/torrents/add' });
  assert.equal(proxy.requests[0].accepted, true);
  assert.deepEqual(proxy.requests[0].returnedJobIds, ['a'.repeat(40)]);
  result = { added_torrent_ids: [], failure_count: 1, pending_count: 0, success_count: 0 };
  await request(url, { method: 'POST', path: '/api/v2/torrents/add' });
  assert.equal(proxy.requests[1].accepted, false);
});
