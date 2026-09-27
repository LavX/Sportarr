import test from 'node:test';
import assert from 'node:assert/strict';
import { validateSmokeTarget } from './smoke.mjs';

test('rejects production and non-isolated media paths before sending requests', () => {
  for (const app of ['http://sportarr:1867', 'https://sportarr.net', 'http://sv-test.evil:1867',
    'http://sv-other:1867', 'http://sv-b0-app:9999', 'http://sv-b0-app:1867/other']) {
    assert.throws(() => validateSmokeTarget({ app, root: '/data/e2e-lib' }));
  }
  assert.throws(() => validateSmokeTarget({ app: 'http://sv-b0-app:1867', root: '/data/media' }));
  assert.throws(() => validateSmokeTarget({ app: 'http://sv-b0-app:1867', root: '/data/e2e-lib/../media' }));
  assert.doesNotThrow(() => validateSmokeTarget({ app: 'http://sv-b0-app:1867', root: '/data/e2e-lib' }));
});
