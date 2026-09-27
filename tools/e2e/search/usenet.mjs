import { createHash } from 'node:crypto';
import net from 'node:net';

const groupName = 'alt.binaries.sportarr';
const yEncLineLength = 128;

function crc32(buffer) {
  let crc = 0xffffffff;
  for (const byte of buffer) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit += 1) crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1));
  }
  return ((crc ^ 0xffffffff) >>> 0).toString(16).padStart(8, '0');
}

function encodeYEnc(buffer) {
  const lines = [];
  let line = [];
  for (const byte of buffer) {
    let encoded = (byte + 42) & 0xff;
    const values = [0x00, 0x0a, 0x0d, 0x3d].includes(encoded)
      ? [0x3d, (encoded + 64) & 0xff]
      : [encoded];
    if (line.length > 0 && line.length + values.length > yEncLineLength) {
      lines.push(Buffer.from(line));
      line = [];
    }
    line.push(...values);
  }
  if (line.length > 0) lines.push(Buffer.from(line));
  return Buffer.concat(lines.flatMap((value) => [value, Buffer.from('\r\n')]));
}

function xmlEscape(value) {
  return value.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;').replaceAll("'", '&apos;');
}

function assertSafeName(name) {
  if (typeof name !== 'string' || name.length === 0 || name.includes('/') || name.includes('\\') || !/^[\x20-\x7e]+$/.test(name)) {
    throw new TypeError('name must be a safe single-file name');
  }
}

export function createUsenetFixture({ data, name, articleBytes = 512 * 1024 }) {
  if (!Buffer.isBuffer(data)) throw new TypeError('data must be a Buffer');
  if (data.length === 0) throw new TypeError('data must not be empty');
  assertSafeName(name);
  if (!Number.isSafeInteger(articleBytes) || articleBytes <= 0) throw new TypeError('articleBytes must be a positive integer');

  const total = Math.ceil(data.length / articleBytes);
  const fileCrc = crc32(data);
  const identity = createHash('sha256').update(name).update('\0').update(data).digest('hex').slice(0, 24);
  const articles = new Map();
  const articleIds = [];
  const segments = [];

  for (let index = 0; index < total; index += 1) {
    const partNumber = index + 1;
    const start = index * articleBytes;
    const part = data.subarray(start, Math.min(start + articleBytes, data.length));
    const articleId = `<${identity}.${partNumber}@fixture.invalid>`;
    const subject = `${name} (${partNumber}/${total})`;
    const yBegin = `=ybegin part=${partNumber} total=${total} line=${yEncLineLength} size=${data.length} name=${name}\r\n`;
    const yPart = `=ypart begin=${start + 1} end=${start + part.length}\r\n`;
    const completeCrc = partNumber === total ? ` crc32=${fileCrc}` : '';
    const yEnd = `=yend size=${part.length} part=${partNumber} pcrc32=${crc32(part)}${completeCrc}\r\n`;
    const headers = [
      'From: Sportarr Fixture <fixture@fixture.invalid>',
      `Newsgroups: ${groupName}`,
      `Subject: ${subject}`,
      `Message-ID: ${articleId}`,
      'Date: Thu, 01 Jan 1970 00:00:00 +0000',
      'Content-Transfer-Encoding: binary',
      '',
      '',
    ].join('\r\n');
    const article = Buffer.concat([
      Buffer.from(headers, 'ascii'),
      Buffer.from(yBegin + yPart, 'ascii'),
      encodeYEnc(part),
      Buffer.from(yEnd, 'ascii'),
    ]);
    articles.set(articleId, article);
    articleIds.push(articleId);
    segments.push(`        <segment bytes="${article.length}" number="${partNumber}">${articleId.slice(1, -1)}</segment>`);
  }

  const escapedName = xmlEscape(name);
  const nzb = Buffer.from([
    '<?xml version="1.0" encoding="UTF-8"?>',
    '<nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">',
    `  <file poster="Sportarr Fixture &lt;fixture@fixture.invalid&gt;" date="0" subject="${escapedName} (1/${total})">`,
    '    <groups>',
    `      <group>${groupName}</group>`,
    '    </groups>',
    '    <segments>',
    ...segments,
    '    </segments>',
    '  </file>',
    '</nzb>',
    '',
  ].join('\n'));

  return { nzb, articles, articleIds, crc32: fileCrc, size: data.length, name };
}

function splitArticle(article) {
  const separator = article.indexOf(Buffer.from('\r\n\r\n'));
  if (separator < 0) return { head: article, body: Buffer.alloc(0) };
  return { head: article.subarray(0, separator), body: article.subarray(separator + 4) };
}

function multiline(socket, status, data, onComplete) {
  socket.write(`${status}\r\n`);
  if (data.length === 0) {
    socket.write('.\r\n', onComplete);
    return;
  }
  const normalized = data.toString('latin1').replaceAll('\r\n', '\n').replaceAll('\r', '\n');
  const payload = normalized.replace(/\n$/, '').split('\n')
    .map((line) => `${line.startsWith('.') ? '.' : ''}${line}\r\n`).join('') + '.\r\n';
  socket.write(payload, 'latin1', onComplete);
}

export function createNntpServer({ articles, username, password, hostname = 'fixture.invalid' }) {
  if (!(articles instanceof Map)) throw new TypeError('articles must be a Map');
  if ((username === undefined) !== (password === undefined)) throw new TypeError('username and password must be provided together');
  const entries = [...articles.entries()];
  const articleNumber = new Map(entries.map(([id], index) => [id, index + 1]));

  const server = net.createServer((socket) => {
    let pending = '';
    let pendingUser;
    let authenticated = username === undefined;
    const inFlight = new Set();
    socket.setEncoding('utf8');
    socket.write(`201 ${hostname} fixture server ready\r\n`);
    socket.on('error', (error) => {
      for (const request of inFlight) request.bytesServed = 0;
      inFlight.clear();
      const record = { code: error.code ?? 'UNKNOWN', message: error.message };
      server.connectionErrors.push(record);
      server.emit('connectionError', record);
    });

    socket.on('data', (chunk) => {
      pending += chunk;
      let boundary;
      while ((boundary = pending.indexOf('\r\n')) >= 0) {
        const line = pending.slice(0, boundary);
        pending = pending.slice(boundary + 2);
        const space = line.indexOf(' ');
        const command = (space < 0 ? line : line.slice(0, space)).toUpperCase();
        const argument = space < 0 ? '' : line.slice(space + 1);
        if (!['BODY', 'ARTICLE', 'STAT'].includes(command)) server.requests.push({ command });

        if (command === 'CAPABILITIES') {
          const capabilities = ['VERSION 2', 'READER'];
          if (username !== undefined) capabilities.push('AUTHINFO USER');
          multiline(socket, '101 Capability list follows', Buffer.from(capabilities.join('\r\n')));
        } else if (command === 'MODE' && argument.toUpperCase() === 'READER') {
          socket.write('201 Reader mode\r\n');
        } else if (command === 'AUTHINFO' && argument.toUpperCase().startsWith('USER ')) {
          pendingUser = argument.slice(5);
          socket.write(pendingUser === username ? '381 Password required\r\n' : '481 Authentication rejected\r\n');
        } else if (command === 'AUTHINFO' && argument.toUpperCase().startsWith('PASS ')) {
          if (pendingUser === undefined) socket.write('482 Authentication commands issued out of sequence\r\n');
          else if (pendingUser === username && argument.slice(5) === password) {
            authenticated = true;
            socket.write('281 Authentication accepted\r\n');
          } else socket.write('481 Authentication rejected\r\n');
          pendingUser = undefined;
        } else if (command === 'DATE') {
          const now = new Date();
          const stamp = now.toISOString().replace(/[-:T]/g, '').slice(0, 14);
          socket.write(`111 ${stamp}\r\n`);
        } else if (!authenticated) {
          socket.write('480 Authentication required\r\n');
        } else if (command === 'LIST') {
          multiline(socket, '215 List of newsgroups follows', Buffer.from(`${groupName} ${entries.length} 1 y`));
        } else if (command === 'GROUP' && argument === groupName) {
          const last = Math.max(entries.length, 1);
          socket.write(`211 ${entries.length} 1 ${last} ${groupName}\r\n`);
        } else if (['BODY', 'ARTICLE', 'STAT'].includes(command)) {
          const article = articles.get(argument);
          if (!article) {
            server.requests.push({ command, articleId: argument, found: false, bytesServed: 0 });
            socket.write('430 No article with that message-id\r\n');
            continue;
          }
          const number = articleNumber.get(argument);
          if (command === 'STAT') {
            server.requests.push({ command, articleId: argument, found: true, bytesServed: 0 });
            socket.write(`223 ${number} ${argument} article exists\r\n`);
          }
          else {
            const parts = splitArticle(article);
            const data = command === 'BODY' ? parts.body : article;
            const request = { command, articleId: argument, found: true, bytesServed: 0 };
            server.requests.push(request);
            inFlight.add(request);
            multiline(socket, `${command === 'BODY' ? 222 : 220} ${number} ${argument} ${command.toLowerCase()} follows`, data, (error) => {
              inFlight.delete(request);
              if (!error) request.bytesServed = data.length;
            });
          }
        } else if (command === 'QUIT') {
          socket.end('205 Closing connection\r\n');
        } else {
          socket.write('500 Command not recognized\r\n');
        }
      }
    });
  });
  server.requests = [];
  server.connectionErrors = [];
  return server;
}
