import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { test } from 'node:test';

import { createTorrent } from './torrent.mjs';

function decode(buffer, cursor = { value: 0 }) {
  const marker = buffer[cursor.value];
  if (marker === 0x69) {
    const end = buffer.indexOf(0x65, cursor.value);
    const value = Number(buffer.subarray(cursor.value + 1, end).toString());
    cursor.value = end + 1;
    return value;
  }
  if (marker === 0x64) {
    cursor.value += 1;
    const value = {};
    while (buffer[cursor.value] !== 0x65) {
      const key = decode(buffer, cursor).toString();
      value[key] = decode(buffer, cursor);
    }
    cursor.value += 1;
    return value;
  }
  const colon = buffer.indexOf(0x3a, cursor.value);
  const length = Number(buffer.subarray(cursor.value, colon).toString());
  cursor.value = colon + 1;
  const value = buffer.subarray(cursor.value, cursor.value + length);
  cursor.value += length;
  return value;
}

test('createTorrent emits a sorted single-file webseed torrent and its info hash', () => {
  const result = createTorrent({
    data: Buffer.from('fixture video'),
    name: 'event.mkv',
    webSeedUrl: 'http://fixture/payload/event.mkv',
  });
  const decoded = decode(result.torrent);
  assert.deepEqual(Object.keys(decoded), ['info', 'url-list']);
  assert.deepEqual(Object.keys(decoded.info), ['length', 'name', 'piece length', 'pieces']);
  assert.equal(decoded.info.length, 13);
  assert.equal(decoded.info.name.toString(), 'event.mkv');
  assert.equal(decoded.info['piece length'], 262144);
  assert.deepEqual(decoded.info.pieces, createHash('sha1').update('fixture video').digest());
  assert.equal(decoded['url-list'].toString(), 'http://fixture/payload/event.mkv');
  assert.equal(result.length, 13);

  const infoStart = result.torrent.indexOf(Buffer.from('4:info')) + 6;
  const infoEnd = result.torrent.indexOf(Buffer.from('8:url-list'));
  assert.equal(result.infoHash, createHash('sha1').update(result.torrent.subarray(infoStart, infoEnd)).digest('hex'));
});

test('createTorrent hashes each 256 KiB piece in order', () => {
  const data = Buffer.alloc(262144 + 3, 0x61);
  const decoded = decode(createTorrent({ data, name: 'large.mkv', webSeedUrl: 'http://fixture/large.mkv' }).torrent);
  const expected = Buffer.concat([
    createHash('sha1').update(data.subarray(0, 262144)).digest(),
    createHash('sha1').update(data.subarray(262144)).digest(),
  ]);
  assert.deepEqual(decoded.info.pieces, expected);
});

test('createTorrent rejects missing input and unsafe file names', () => {
  assert.throws(() => createTorrent({ data: 'text', name: 'event.mkv', webSeedUrl: 'http://fixture/event.mkv' }), /Buffer/);
  assert.throws(() => createTorrent({ data: Buffer.alloc(1), name: '../event.mkv', webSeedUrl: 'http://fixture/event.mkv' }), /name/);
  assert.throws(() => createTorrent({ data: Buffer.alloc(1), name: 'event.mkv', webSeedUrl: '' }), /webSeedUrl/);
});
