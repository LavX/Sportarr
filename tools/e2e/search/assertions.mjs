import assert from 'node:assert/strict';

export const RESULT_STATUSES = Object.freeze(['PASS', 'FAIL', 'BLOCKED']);

const requiredFields = [
  'routeId', 'variantId', 'actions', 'expectation', 'caseId', 'provider', 'sourceType', 'clientType', 'cacheState', 'buildSha',
  'status', 'expectedNeeds', 'expectedReleaseIds', 'returnedReleaseIds', 'selectedReleaseIds', 'importedNeeds',
  'unexpectedAssignments', 'clientAdds', 'duplicateAdds', 'outboundAttempts',
  'quotaViolations', 'fileChecks', 'elapsedMs', 'evidencePaths',
];

function sameMembers(left, right) {
  return left.length === right.length && [...left].sort().every((value, index) => value === [...right].sort()[index]);
}

export function validateResult(result) {
  const errors = [];
  if (!result || typeof result !== 'object' || Array.isArray(result)) return ['result must be an object'];
  for (const field of requiredFields) {
    if (!(field in result)) errors.push(`${field} is required`);
  }
  for (const field of ['routeId', 'variantId', 'caseId', 'provider', 'sourceType', 'clientType', 'cacheState', 'buildSha']) {
    if (field in result && (typeof result[field] !== 'string' || result[field].length === 0)) errors.push(`${field} must be a non-empty string`);
  }
  if ('status' in result && !RESULT_STATUSES.includes(result.status)) errors.push('status must be PASS, FAIL, or BLOCKED');
  for (const field of ['actions', 'expectedNeeds', 'expectedReleaseIds', 'returnedReleaseIds', 'selectedReleaseIds', 'importedNeeds', 'unexpectedAssignments', 'fileChecks', 'evidencePaths']) {
    if (field in result && !Array.isArray(result[field])) errors.push(`${field} must be an array`);
  }
  for (const field of ['clientAdds', 'duplicateAdds', 'outboundAttempts', 'quotaViolations', 'elapsedMs']) {
    if (field in result && result[field] !== null && (!Number.isInteger(result[field]) || result[field] < 0)) errors.push(`${field} must be null or a non-negative integer`);
  }
  if ('expectation' in result && !['IMPORT', 'NO_ACQUISITION', 'PENDING', 'RECOVERABLE_FAILURE', 'CANCELLED'].includes(result.expectation)) errors.push('expectation is invalid');
  return errors;
}

export function assessCase(result, route = { required: true }) {
  const errors = validateResult(result);
  if (errors.length > 0) return { outcome: 'FAIL', errors };
  if (Array.isArray(route.variants) && !route.variants.includes(result.variantId)) errors.push('variantId is not declared for the route');
  if (Array.isArray(route.actions) && result.actions.some(action => !route.actions.includes(action))) errors.push('actions contain an undeclared route action');
  if (errors.length > 0) return { outcome: 'FAIL', errors };
  if (result.status === 'BLOCKED') {
    return { outcome: route.required === false ? 'BLOCKED' : 'INCOMPLETE', errors: [] };
  }
  if (result.status === 'FAIL') return { outcome: 'FAIL', errors: [...errors, 'runner reported FAIL'] };
  if (result.expectation === 'IMPORT' && !sameMembers(result.importedNeeds, result.expectedNeeds)) errors.push('importedNeeds must exactly match expectedNeeds for an import');
  if (result.selectedReleaseIds.some(id => !result.expectedReleaseIds.includes(id))) errors.push('selectedReleaseIds must contain only expected releases');
  if (result.unexpectedAssignments.length > 0) errors.push('unexpectedAssignments must be empty');
  if (result.duplicateAdds !== null && result.duplicateAdds !== 0) errors.push('duplicateAdds must be zero');
  if (result.quotaViolations !== null && result.quotaViolations !== 0) errors.push('quotaViolations must be zero');
  if (result.expectation === 'IMPORT' && result.fileChecks.length === 0) errors.push('fileChecks must contain real file evidence');
  if (result.fileChecks.some(file => typeof file?.path !== 'string' || !file.path || typeof file?.payloadId !== 'string' || !file.payloadId)) errors.push('every file check must identify a path and payload');
  if (result.fileChecks.some(file => typeof file?.expectedHash !== 'string' || !file.expectedHash || file.expectedHash !== file.actualHash)) errors.push('every file must match its expected content hash');
  if (result.fileChecks.some(file => file?.contentVerified !== true)) errors.push('every file must have verified content');
  if (result.fileChecks.some(file => file?.insideTestRoot !== true)) errors.push('every file must be inside the test root');
  if (result.expectation === 'NO_ACQUISITION' && result.clientAdds !== null && result.clientAdds !== 0) errors.push('a no-acquisition case must not add a client job');
  if (result.expectation !== 'IMPORT' && result.importedNeeds.length > 0) errors.push('a non-import case must not report imported needs');
  if (result.expectation === 'PENDING' && ((result.clientAdds !== null && result.clientAdds !== 0) || result.outcomeEvidence?.state !== 'PENDING' || result.outcomeEvidence?.preEligibilityClientAdds !== 0)) errors.push('a pending case must prove no pre-eligibility dispatch');
  if (result.expectation === 'RECOVERABLE_FAILURE' && (result.outcomeEvidence?.state !== 'FAILED' || result.outcomeEvidence?.recoveryVerified !== true || !result.outcomeEvidence?.payloadId || !result.outcomeEvidence?.path || !result.outcomeEvidence?.hash)) errors.push('a recoverable failure must identify and verify the recoverable payload');
  if (result.expectation === 'CANCELLED' && ((result.clientAdds !== null && result.clientAdds !== 0) || result.outcomeEvidence?.state !== 'CANCELLED' || result.outcomeEvidence?.dispatchStopped !== true)) errors.push('a cancelled case must prove settled cancellation and stopped dispatch');
  if (result.evidencePaths.length === 0 || result.evidencePaths.some(evidence => typeof evidence?.path !== 'string' || !evidence.path || typeof evidence?.kind !== 'string' || !evidence.kind || evidence?.exists !== true)) {
    errors.push('evidencePaths must contain evidence');
  }
  const unmeasured = ['clientAdds', 'duplicateAdds', 'outboundAttempts', 'quotaViolations', 'elapsedMs'].filter(field => result[field] === null);
  if (result.releaseMetrics?.recall === null) unmeasured.push('releaseRecall');
  if (result.releaseMetrics?.precision === null) unmeasured.push('releasePrecision');
  return { outcome: errors.length > 0 ? 'FAIL' : unmeasured.length > 0 ? 'INCOMPLETE' : 'PASS', errors, unmeasured };
}

export function assertCompletedCase(result) {
  const assessment = assessCase(result);
  assert.equal(assessment.outcome, 'PASS', assessment.errors.join('; '));
}
