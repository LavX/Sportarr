import assert from 'node:assert/strict';
import net from 'node:net';
import { afterEach, test } from 'node:test';

import { createNntpServer, createUsenetFixture } from './usenet.mjs';

const servers = new Set();

afterEach(async () => {
  await Promise.all([...servers].map((server) => new Promise((resolve) => server.close(resolve))));
  servers.clear();
});

function crc32(buffer) {
  let crc = 0xffffffff;
  for (const byte of buffer) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit += 1) crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1));
  }
  return ((crc ^ 0xffffffff) >>> 0).toString(16).padStart(8, '0');
}

function decodeArticle(article) {
  const separator = article.indexOf(Buffer.from('\r\n\r\n'));
  const lines = article.subarray(separator + 4).toString('latin1').split('\r\n');
  const begin = lines.find((line) => line.startsWith('=ybegin '));
  const part = lines.find((line) => line.startsWith('=ypart '));
  const end = lines.find((line) => line.startsWith('=yend '));
  const encoded = lines.slice(lines.indexOf(part) + 1, lines.indexOf(end)).join('');
  const bytes = [];
  for (let index = 0; index < encoded.length; index += 1) {
    let value = encoded.charCodeAt(index);
    if (value === 61) value = (encoded.charCodeAt(++index) - 64 + 256) % 256;
    bytes.push((value - 42 + 256) % 256);
  }
  const fields = (line) => Object.fromEntries([...line.matchAll(/(?:^| )(\w+)=([^ ]+)/g)].map((match) => [match[1], match[2]]));
  return { data: Buffer.from(bytes), begin: fields(begin), part: fields(part), end: fields(end) };
}

function nzbSegments(nzb) {
  return [...nzb.toString().matchAll(/<segment bytes="(\d+)" number="(\d+)">([^<]+)<\/segment>/g)]
    .map((match) => ({ bytes: Number(match[1]), number: Number(match[2]), id: `<${match[3]}>` }));
}

async function startServer(options) {
  const server = createNntpServer(options);
  servers.add(server);
  await new Promise((resolve, reject) => server.listen(0, '127.0.0.1', resolve).once('error', reject));
  return server.address().port;
}

async function session(port, commands) {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection({ host: '127.0.0.1', port });
    const chunks = [];
    socket.on('data', (chunk) => chunks.push(chunk));
    socket.once('error', reject);
    socket.once('close', () => resolve(Buffer.concat(chunks).toString('latin1')));
    socket.once('connect', () => socket.write(`${commands.join('\r\n')}\r\n`));
  });
}

test('createUsenetFixture reconstructs every byte and publishes independent checksums', () => {
  const data = Buffer.from([...Array(1025)].map((_, index) => index % 256));
  const fixture = createUsenetFixture({ data, name: 'event.mkv', articleBytes: 257 });
  const segments = nzbSegments(fixture.nzb);
  const decoded = segments.map(({ id }, index) => {
    const part = decodeArticle(fixture.articles.get(id));
    assert.equal(part.begin.part, String(index + 1));
    assert.equal(part.begin.total, '4');
    assert.equal(part.begin.size, '1025');
    assert.equal(part.begin.name, 'event.mkv');
    assert.equal(part.part.begin, String(index * 257 + 1));
    assert.equal(part.part.end, String(index * 257 + part.data.length));
    assert.equal(part.end.size, String(part.data.length));
    assert.equal(part.end.part, String(index + 1));
    assert.equal(part.end.pcrc32, crc32(part.data));
    return part.data;
  });
  assert.deepEqual(Buffer.concat(decoded), data);
  assert.equal(fixture.crc32, '00b8613c');
  assert.equal(crc32(Buffer.concat(decoded)), fixture.crc32);
  assert.deepEqual(segments.map(({ number }) => number), [1, 2, 3, 4]);
  assert.deepEqual(segments.map(({ bytes }) => bytes), [...fixture.articles.values()].map((article) => article.length));
});

test('createUsenetFixture escapes yEnc control bytes and rejects unsafe input', () => {
  const fixture = createUsenetFixture({ data: Buffer.from([214, 224, 227, 19]), name: 'safe.mkv' });
  const article = fixture.articles.values().next().value;
  assert.match(article.toString('latin1'), /=ybegin part=1 total=1 line=128 size=4 name=safe\.mkv\r\n/);
  assert.deepEqual(decodeArticle(article).data, Buffer.from([214, 224, 227, 19]));
  assert.throws(() => createUsenetFixture({ data: 'bad', name: 'safe.mkv' }), /Buffer/);
  assert.throws(() => createUsenetFixture({ data: Buffer.alloc(1), name: '../unsafe.mkv' }), /name/);
  assert.throws(() => createUsenetFixture({ data: Buffer.alloc(1), name: 'unsafe\r\nSubject: injected.mkv' }), /name/);
  assert.throws(() => createUsenetFixture({ data: Buffer.alloc(1), name: 'event😀.mkv' }), /name/);
  assert.throws(() => createUsenetFixture({ data: Buffer.alloc(1), name: 'safe.mkv', articleBytes: 0 }), /articleBytes/);
});

test('NNTP server supports discovery, reader mode, group selection, and authentication', async () => {
  const fixture = createUsenetFixture({ data: Buffer.from('fixture'), name: 'event.mkv' });
  const port = await startServer({ articles: fixture.articles, username: 'reader', password: 'secret' });
  const response = await session(port, [
    'CAPABILITIES', 'MODE READER', 'AUTHINFO USER reader', 'AUTHINFO PASS secret',
    'DATE', 'LIST', 'GROUP alt.binaries.sportarr', 'QUIT',
  ]);
  assert.match(response, /^201 .*\r\n/);
  assert.match(response, /101 Capability list follows\r\nVERSION 2\r\nREADER\r\nAUTHINFO USER\r\n\.\r\n/);
  assert.match(response, /201 Reader mode\r\n381 Password required\r\n281 Authentication accepted\r\n/);
  assert.match(response, /111 \d{14}\r\n/);
  assert.match(response, /215 List of newsgroups follows\r\nalt\.binaries\.sportarr 1 1 y\r\n\.\r\n/);
  assert.match(response, /211 1 1 1 alt\.binaries\.sportarr\r\n205 Closing connection\r\n$/);
});

test('NNTP server retrieves BODY, ARTICLE, and STAT by message ID with dot-stuffing', async () => {
  const articleId = '<dot@fixture.invalid>';
  const articles = new Map([[articleId, Buffer.from([
    'From: Fixture <fixture@fixture.invalid>',
    `Message-ID: ${articleId}`,
    'Newsgroups: alt.binaries.sportarr',
    'Subject: dots',
    '',
    '.first',
    '..second',
    'last',
  ].join('\r\n'), 'latin1')]]);
  const port = await startServer({ articles });
  const response = await session(port, [`STAT ${articleId}`, `BODY ${articleId}`, `ARTICLE ${articleId}`, 'QUIT']);
  assert.match(response, /223 1 <dot@fixture\.invalid> article exists\r\n/);
  assert.match(response, /222 1 <dot@fixture\.invalid> body follows\r\n\.\.first\r\n\.\.\.second\r\nlast\r\n\.\r\n/);
  assert.match(response, /220 1 <dot@fixture\.invalid> article follows\r\nFrom: Fixture/);
  assert.equal((response.match(/\.\.first\r\n/g) ?? []).length, 2);
});

test('NNTP server reports missing articles and serves concurrent connections', async () => {
  const fixture = createUsenetFixture({ data: Buffer.from('concurrent'), name: 'event.mkv' });
  const id = fixture.articleIds[0];
  const port = await startServer({ articles: fixture.articles });
  const [first, second] = await Promise.all([
    session(port, [`BODY ${id}`, 'QUIT']),
    session(port, ['STAT <missing@fixture.invalid>', `STAT ${id}`, 'QUIT']),
  ]);
  assert.match(first, /222 1 .* body follows/);
  assert.match(second, /430 No article with that message-id\r\n223 1 .* article exists/);
});

test('NNTP wire retrieval un-stuffs and reconstructs generated binary bytes', async () => {
  const data = Buffer.from([...Array(256)].map((_, index) => index));
  const fixture = createUsenetFixture({ data, name: 'wire.mkv', articleBytes: 256 });
  const port = await startServer({ articles: fixture.articles });
  const response = await session(port, [`BODY ${fixture.articleIds[0]}`, 'QUIT']);
  const bodyStart = response.indexOf('\r\n') + 2;
  const bodyEnd = response.indexOf('\r\n.\r\n', bodyStart);
  const unstuffed = response.slice(bodyStart, bodyEnd).replace(/^\.\./gm, '.');
  const article = Buffer.concat([Buffer.from('\r\n\r\n'), Buffer.from(unstuffed, 'latin1')]);
  assert.deepEqual(decodeArticle(article).data, data);
});

test('NNTP server emits an empty multiline body without an extra data line', async () => {
  const id = '<empty@fixture.invalid>';
  const article = Buffer.from(`Message-ID: ${id}\r\n\r\n`);
  const port = await startServer({ articles: new Map([[id, article]]) });
  const response = await session(port, [`BODY ${id}`, 'QUIT']);
  assert.match(response, /body follows\r\n\.\r\n205 Closing connection/);
  assert.doesNotMatch(response, /body follows\r\n\r\n\.\r\n/);
});

test('NNTP server contains reset connections and continues serving requests', async () => {
  const fixture = createUsenetFixture({ data: Buffer.from('reset-safe'), name: 'event.mkv' });
  const server = createNntpServer({ articles: fixture.articles });
  servers.add(server);
  await new Promise((resolve, reject) => server.listen(0, '127.0.0.1', resolve).once('error', reject));
  const port = server.address().port;
  const resetObserved = new Promise((resolve) => server.once('connectionError', resolve));
  await new Promise((resolve, reject) => {
    const socket = net.createConnection({ host: '127.0.0.1', port });
    socket.once('error', reject);
    socket.once('data', () => {
      socket.resetAndDestroy();
      resolve();
    });
  });
  await resetObserved;
  const response = await session(port, [`STAT ${fixture.articleIds[0]}`, 'QUIT']);
  assert.match(response, /223 1 .* article exists/);
  assert.equal(server.connectionErrors.length, 1);
  assert.ok(server.connectionErrors.every(({ code }) => code === 'ECONNRESET'));
});

test('NNTP request ledger does not count a BODY aborted by a reset as served', async () => {
  const data = Buffer.alloc(16 * 1024 * 1024, 0x61);
  const fixture = createUsenetFixture({ data, name: 'large.mkv', articleBytes: data.length });
  const server = createNntpServer({ articles: fixture.articles });
  servers.add(server);
  await new Promise((resolve, reject) => server.listen(0, '127.0.0.1', resolve).once('error', reject));
  const resetObserved = new Promise((resolve) => server.once('connectionError', resolve));
  await new Promise((resolve, reject) => {
    const socket = net.createConnection({ host: '127.0.0.1', port: server.address().port });
    let receivedGreeting = false;
    socket.once('error', reject);
    socket.on('data', () => {
      if (!receivedGreeting) {
        receivedGreeting = true;
        socket.write(`BODY ${fixture.articleIds[0]}\r\n`);
      } else {
        socket.resetAndDestroy();
        resolve();
      }
    });
  });
  await resetObserved;
  await new Promise((resolve) => setImmediate(resolve));
  assert.deepEqual(server.requests, [
    { command: 'BODY', articleId: fixture.articleIds[0], found: true, bytesServed: 0 },
  ]);
});

test('NNTP request ledger records retrieval outcomes without credentials', async () => {
  const fixture = createUsenetFixture({ data: Buffer.from('ledger'), name: 'event.mkv' });
  const article = fixture.articles.get(fixture.articleIds[0]);
  const bodyBytes = article.length - article.indexOf(Buffer.from('\r\n\r\n')) - 4;
  const server = createNntpServer({ articles: fixture.articles, username: 'reader', password: 'super-secret' });
  servers.add(server);
  await new Promise((resolve, reject) => server.listen(0, '127.0.0.1', resolve).once('error', reject));
  await session(server.address().port, [
    'AUTHINFO USER reader', 'AUTHINFO PASS super-secret',
    `BODY ${fixture.articleIds[0]}`, 'STAT <missing@fixture.invalid>', 'QUIT',
  ]);
  assert.deepEqual(server.requests, [
    { command: 'AUTHINFO' },
    { command: 'AUTHINFO' },
    { command: 'BODY', articleId: fixture.articleIds[0], found: true, bytesServed: bodyBytes },
    { command: 'STAT', articleId: '<missing@fixture.invalid>', found: false, bytesServed: 0 },
    { command: 'QUIT' },
  ]);
  assert.doesNotMatch(JSON.stringify(server.requests), /reader|super-secret/);
});
