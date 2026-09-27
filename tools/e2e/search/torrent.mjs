import { createHash } from 'node:crypto';

const defaultPieceLength = 256 * 1024;

function bencode(value) {
  if (Buffer.isBuffer(value)) return Buffer.concat([Buffer.from(`${value.length}:`), value]);
  if (typeof value === 'string') return bencode(Buffer.from(value));
  if (Number.isSafeInteger(value) && value >= 0) return Buffer.from(`i${value}e`);
  if (value && typeof value === 'object' && !Array.isArray(value)) {
    const entries = Object.entries(value)
      .map(([key, item]) => [Buffer.from(key), item])
      .sort(([left], [right]) => Buffer.compare(left, right));
    return Buffer.concat([
      Buffer.from('d'),
      ...entries.flatMap(([key, item]) => [bencode(key), bencode(item)]),
      Buffer.from('e'),
    ]);
  }
  throw new TypeError('Unsupported bencode value');
}

export function createTorrent({ data, name, webSeedUrl }) {
  if (!Buffer.isBuffer(data)) throw new TypeError('data must be a Buffer');
  if (typeof name !== 'string' || name.length === 0 || name.includes('/') || name.includes('\\') || name.includes('\0')) {
    throw new TypeError('name must be a safe single-file name');
  }
  if (typeof webSeedUrl !== 'string' || webSeedUrl.length === 0) throw new TypeError('webSeedUrl is required');
  let parsedUrl;
  try {
    parsedUrl = new URL(webSeedUrl);
  } catch {
    throw new TypeError('webSeedUrl must be an HTTP URL');
  }
  if (!['http:', 'https:'].includes(parsedUrl.protocol)) throw new TypeError('webSeedUrl must be an HTTP URL');

  const hashes = [];
  for (let offset = 0; offset < data.length; offset += defaultPieceLength) {
    hashes.push(createHash('sha1').update(data.subarray(offset, offset + defaultPieceLength)).digest());
  }
  const info = {
    length: data.length,
    name,
    'piece length': defaultPieceLength,
    pieces: Buffer.concat(hashes),
  };
  const encodedInfo = bencode(info);
  return {
    torrent: Buffer.concat([
      Buffer.from('d4:info'),
      encodedInfo,
      bencode('url-list'),
      bencode(webSeedUrl),
      Buffer.from('e'),
    ]),
    infoHash: createHash('sha1').update(encodedInfo).digest('hex'),
    length: data.length,
  };
}
