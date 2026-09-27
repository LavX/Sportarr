import test from 'node:test';
import assert from 'node:assert/strict';
import { validateRig, runWorkerProcess } from './run.mjs';

const rig = () => ({ buildSha: 'a'.repeat(40), publishArchive: '/evidence/publish.tar.gz', uiArchive: '/evidence/ui.tar.gz',
  configXml: '/evidence/config.xml', payloadFile: '/evidence/fixture.mkv', buildEvidenceFile: '/evidence/build-evidence.json',
  mediaHostBase: '/mnt/user/data/e2e-lib/search-validation',
  images: Object.fromEntries(['app', 'node', 'qbit', 'postgres'].map(key => [key, `sha256:${'b'.repeat(64)}`])),
  expectedHashes: Object.fromEntries(['publishArchive', 'uiArchive', 'configXml', 'payloadFile', 'buildEvidenceFile'].map(key => [key, 'c'.repeat(64)])) });

test('requires pinned images and a dedicated media root', () => {
  assert.doesNotThrow(() => validateRig(rig()));
  const live = rig(); live.mediaHostBase = '/mnt/user/data/media';
  assert.throws(() => validateRig(live));
  const tag = rig(); tag.images.app = 'sportarr:latest';
  assert.throws(() => validateRig(tag));
  const traversal = rig(); traversal.mediaHostBase += '/../..';
  assert.throws(() => validateRig(traversal));
});

test('an interrupted worker exits so resource cleanup can run', async () => {
  const controller = new AbortController();
  const result = runWorkerProcess(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], controller.signal);
  setTimeout(() => controller.abort(), 30);
  const exit = await result;
  assert.notEqual(exit.status, 0);
  assert.equal(exit.interrupted, true);
});

test('classifies setup and transport failures separately from measured acquisition assertions', async () => {
  const { failureKind } = await import('./run.mjs');
  assert.equal(failureKind(new TypeError('fetch failed'), { phase: 'acquisition' }), 'infrastructure');
  assert.equal(failureKind({ code: 'ERR_ASSERTION' }), 'infrastructure');
  assert.equal(failureKind({ code: 'ERR_ASSERTION' }, { phase: 'acquisition' }), 'acquisition');
  assert.equal(failureKind({ failureCategory: 'acquisition' }, { interrupted: true }), 'interrupted');
});

test('cleanup preserves an earlier product failure and records its own failure when alone', async () => {
  const { combineCleanupFailure } = await import('./run.mjs');
  const original = new Error('part mismatch');
  assert.deepEqual(combineCleanupFailure(original, 'acquisition', true), { failure: original, category: 'acquisition' });
  assert.equal(combineCleanupFailure(undefined, undefined, true).category, 'cleanup');
});
