import test from 'node:test';
import assert from 'node:assert/strict';

import { assessCase, assertCompletedCase } from './assertions.mjs';

function validResult(overrides = {}) {
  return {
    routeId: 'R01', variantId: 'page and modal entry points', actions: ['POST /api/event/{id}/search', 'POST /api/release/grab'],
    expectation: 'IMPORT', caseId: 'case-1', provider: 'sqlite', sourceType: 'torznab',
    clientType: 'qbittorrent', cacheState: 'cold', buildSha: 'abc', status: 'PASS',
    expectedNeeds: ['event:1:main'], expectedReleaseIds: ['release-1'], returnedReleaseIds: ['release-1'],
    selectedReleaseIds: ['release-1'], importedNeeds: ['event:1:main'],
    unexpectedAssignments: [], clientAdds: 1, duplicateAdds: 0, outboundAttempts: 1,
    quotaViolations: 0, fileChecks: [{ path: '/test/event.mkv', payloadId: 'payload-1', expectedHash: 'hash-1', actualHash: 'hash-1', contentVerified: true, insideTestRoot: true }],
    elapsedMs: 100, releaseMetrics: { recall: 1, precision: 1 }, evidencePaths: [{ path: 'requests.ndjson', kind: 'requests', exists: true }, { path: 'files.json', kind: 'files', exists: true }],
    ...overrides,
  };
}

test('accepts a completed case with verified evidence', () => {
  assert.equal(assessCase(validResult()).outcome, 'PASS');
  assert.doesNotThrow(() => assertCompletedCase(validResult()));
});

for (const [name, override] of [
  ['missing expected import', { importedNeeds: [] }],
  ['wrong assignment', { unexpectedAssignments: ['event:2:main'] }],
  ['duplicate add', { duplicateAdds: 1 }],
  ['quota violation', { quotaViolations: 1 }],
  ['missing file check', { fileChecks: [] }],
  ['unverified file content', { fileChecks: [{ path: '/test/event.mkv', payloadId: 'payload-1', expectedHash: 'hash-1', actualHash: 'hash-1', contentVerified: false, insideTestRoot: true }] }],
  ['file outside test root', { fileChecks: [{ path: '/outside/event.mkv', payloadId: 'payload-1', expectedHash: 'hash-1', actualHash: 'hash-1', contentVerified: true, insideTestRoot: false }] }],
  ['missing evidence', { evidencePaths: [] }],
]) {
  test(`fails a completed case with ${name}`, () => {
    const assessment = assessCase(validResult(override));
    assert.equal(assessment.outcome, 'FAIL');
    assert.ok(assessment.errors.length > 0);
    assert.throws(() => assertCompletedCase(validResult(override)));
  });
}

test('keeps a required blocked route incomplete', () => {
  const assessment = assessCase(validResult({ status: 'BLOCKED' }), { required: true });
  assert.equal(assessment.outcome, 'INCOMPLETE');
});

test('accepts an evidenced negative case without fabricated file checks', () => {
  const assessment = assessCase(validResult({ expectation: 'NO_ACQUISITION', expectedNeeds: [], importedNeeds: [], clientAdds: 0, fileChecks: [] }));
  assert.equal(assessment.outcome, 'PASS');
});

test('fails a negative case that adds a client job', () => {
  const assessment = assessCase(validResult({ expectation: 'NO_ACQUISITION', expectedNeeds: [], importedNeeds: [], clientAdds: 1, fileChecks: [] }));
  assert.equal(assessment.outcome, 'FAIL');
});

test('requires pending dispatch evidence', () => {
  const good = validResult({ expectation: 'PENDING', importedNeeds: [], clientAdds: 0, fileChecks: [], outcomeEvidence: { state: 'PENDING', preEligibilityClientAdds: 0 } });
  assert.equal(assessCase(good).outcome, 'PASS');
  assert.equal(assessCase({ ...good, outcomeEvidence: { state: 'PENDING', preEligibilityClientAdds: 1 } }).outcome, 'FAIL');
});

test('requires recoverability evidence after a controlled failure', () => {
  const good = validResult({ expectation: 'RECOVERABLE_FAILURE', importedNeeds: [], fileChecks: [], outcomeEvidence: { state: 'FAILED', recoveryVerified: true, payloadId: 'payload-1', path: '/test/payload', hash: 'hash-1' } });
  assert.equal(assessCase(good).outcome, 'PASS');
  assert.equal(assessCase({ ...good, outcomeEvidence: { state: 'FAILED', recoveryVerified: false } }).outcome, 'FAIL');
});

test('requires a settled cancellation with stopped dispatch', () => {
  const good = validResult({ expectation: 'CANCELLED', importedNeeds: [], clientAdds: 0, fileChecks: [], outcomeEvidence: { state: 'CANCELLED', dispatchStopped: true } });
  assert.equal(assessCase(good).outcome, 'PASS');
  assert.equal(assessCase({ ...good, outcomeEvidence: { state: 'RUNNING', dispatchStopped: false } }).outcome, 'FAIL');
});

test('rejects malformed runtime results honestly', () => {
  const assessment = assessCase({ status: 'PASS' });
  assert.equal(assessment.outcome, 'FAIL');
  assert.match(assessment.errors.join('\n'), /routeId/);
});

test('keeps unmeasured gate counters incomplete instead of inventing zero', () => {
  const assessment = assessCase(validResult({ clientAdds: null, duplicateAdds: null, quotaViolations: null }));
  assert.equal(assessment.outcome, 'INCOMPLETE');
  assert.deepEqual(assessment.unmeasured, ['clientAdds', 'duplicateAdds', 'quotaViolations']);
});

test('rejects selection of an unexpected release without treating returned extras as fatal', () => {
  assert.equal(assessCase(validResult({ returnedReleaseIds: ['release-1', 'extra'], releaseMetrics: { recall: null, precision: null } })).outcome, 'INCOMPLETE');
  assert.equal(assessCase(validResult({ selectedReleaseIds: ['extra'] })).outcome, 'FAIL');
});

test('keeps null client counts incomplete for negative outcomes', () => {
  const result = validResult({ expectation: 'NO_ACQUISITION', expectedNeeds: [], importedNeeds: [], clientAdds: null, fileChecks: [] });
  assert.equal(assessCase(result).outcome, 'INCOMPLETE');
});
