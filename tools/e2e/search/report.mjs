#!/usr/bin/env node
import { readFile, stat } from 'node:fs/promises';
import { resolve, sep } from 'node:path';
import { createHash } from 'node:crypto';
import { pathToFileURL } from 'node:url';

import { assessCase } from './assertions.mjs';
import routes from './routes.json' with { type: 'json' };

const routeMap = new Map(routes.map(route => [route.id, route]));
const BASELINE_SHA = '029eb3e4ebb93acfd10c35784247b32f49b59c84';

function stable(value) {
  if (Array.isArray(value)) return value.map(stable);
  if (value && typeof value === 'object') return Object.fromEntries(Object.keys(value).sort().map(key => [key, stable(value[key])]));
  return value;
}

function equal(left, right) {
  return JSON.stringify(stable(left)) === JSON.stringify(stable(right));
}

export function ratio(numerator, denominator) {
  return denominator === 0 ? null : numerator / denominator;
}

function caseKey(row) {
  return [row.routeId, row.variantId, row.caseId, row.provider, row.sourceType, row.clientType, row.cacheState].join('\u001f');
}

function checkComparable(baseline, candidate) {
  for (const [name, manifest, mode] of [['baseline', baseline, 'baseline'], ['candidate', candidate, 'candidate']]) {
    for (const field of ['schemaVersion', 'runId', 'mode', 'build', 'corpus', 'sharedSettings', 'runner', 'provenanceStatus', 'expectedCases', 'treatment']) {
      if (!(field in manifest)) throw new Error(`${name} manifest requires ${field}`);
    }
    if (manifest.mode !== mode) throw new Error(`${name} manifest mode must be ${mode}`);
    if (!manifest.build?.sha || !manifest.build?.imageDigest) throw new Error(`${name} build identity is incomplete`);
    if (!/^[0-9a-f]{40}$/.test(manifest.build.sha) || !/^sha256:[0-9a-f]{64}$/.test(manifest.build.imageDigest)) throw new Error(`${name} build identity must be immutable`);
    if (!manifest.corpus?.hash || !manifest.corpus?.path) throw new Error(`${name} corpus identity is incomplete`);
    if (!manifest.sharedSettings?.hash || !manifest.sharedSettings?.path) throw new Error(`${name} sharedSettings identity is incomplete`);
    if (!manifest.runner?.version || !manifest.runner?.sha) throw new Error(`${name} runner identity is incomplete`);
    if (manifest.provenanceStatus !== 'COMPLETE') throw new Error(`${name} runner provenance is incomplete`);
    if (!Array.isArray(manifest.expectedCases)) throw new Error(`${name} expectedCases must be an array`);
    if (!manifest.treatment || !Array.isArray(manifest.treatment.featureFlags) || Object.keys(manifest.treatment).some(key => key !== 'featureFlags') || manifest.treatment.featureFlags.some(flag => typeof flag !== 'string' || !flag) || new Set(manifest.treatment.featureFlags).size !== manifest.treatment.featureFlags.length) throw new Error(`${name} treatment must declare unique featureFlags only`);
  }
  if (baseline.build.sha !== BASELINE_SHA) throw new Error(`baseline build SHA must be B0 ${BASELINE_SHA}`);
  if (baseline.treatment.featureFlags.length !== 0) throw new Error('baseline treatment featureFlags must be empty');
  for (const field of ['schemaVersion', 'corpus', 'sharedSettings', 'runner', 'provenanceStatus', 'providers', 'expectedCases']) {
    if (!equal(baseline[field], candidate[field])) throw new Error(`${field} differs between baseline and candidate`);
  }
}

function statusOf(row) {
  if (!row) return 'MISSING';
  const route = routeMap.get(row.routeId);
  return route ? assessCase(row, route).outcome : 'FAIL';
}

function changeOf(baselineStatus, candidateStatus) {
  if (baselineStatus === candidateStatus) return 'UNCHANGED';
  if (candidateStatus === 'PASS') return 'IMPROVED';
  if (baselineStatus === 'PASS') return 'REGRESSED';
  if (candidateStatus === 'INCOMPLETE' || candidateStatus === 'MISSING') return 'INCOMPLETE';
  return 'CHANGED';
}

function correctImportedNeeds(row) {
  if (!row) return null;
  const expected = new Set(row.expectedNeeds);
  return new Set(row.importedNeeds.filter(need => expected.has(need))).size;
}

function sum(rows, field) {
  return rows.length === 0 ? null : rows.reduce((total, row) => total + row[field], 0);
}

function aggregateRows(rows, key) {
  const groups = new Map();
  for (const row of rows) {
    const value = String(row[key] ?? 'unknown');
    if (!groups.has(value)) groups.set(value, []);
    groups.get(value).push(row);
  }
  return [...groups.entries()].sort(([left], [right]) => left.localeCompare(right)).map(([value, members]) => {
    const baselinePassed = members.filter(item => item.baselineStatus === 'PASS').length;
    const candidatePassed = members.filter(item => item.candidateStatus === 'PASS').length;
    const baselineRate = ratio(baselinePassed, members.length);
    const candidateRate = ratio(candidatePassed, members.length);
    return {
      key: value, cases: members.length, baselinePassed, candidatePassed,
      passedDelta: candidatePassed - baselinePassed,
      passedRateDelta: baselineRate === null || candidateRate === null ? null : candidateRate - baselineRate,
      passedPercentDelta: baselineRate === 0 || baselineRate === null || candidateRate === null ? null : (candidateRate - baselineRate) / baselineRate,
    };
  });
}

export function determineExitCode(assessments) {
  if (assessments.some(item => item.required !== false && (item.outcome === 'INCOMPLETE' || item.outcome === 'MISSING'))) return 2;
  if (assessments.some(item => item.required !== false && item.outcome === 'FAIL')) return 1;
  return 0;
}

export function gatesEnabled(argv) {
  return !argv.includes('--no-gates');
}

export function compareRuns(baselineRun, candidateRun, { enforceGates = true } = {}) {
  if (!baselineRun?.manifest || !candidateRun?.manifest) throw new Error('both runs require manifest data');
  if (!Array.isArray(baselineRun.results) || !Array.isArray(candidateRun.results)) throw new Error('both runs require results arrays');
  checkComparable(baselineRun.manifest, candidateRun.manifest);
  const indexed = (run, name) => {
    const map = new Map();
    for (const item of run.results) {
      const key = caseKey(item);
      if (map.has(key)) throw new Error(`${name} results contain duplicate case key ${key}`);
      if (item.buildSha !== run.manifest.build.sha) throw new Error(`${name} result buildSha does not match its manifest`);
      map.set(key, item);
    }
    return map;
  };
  const baselineByKey = indexed(baselineRun, 'baseline');
  const candidateByKey = indexed(candidateRun, 'candidate');
  const expectedByKey = new Map(candidateRun.manifest.expectedCases.map(item => [caseKey(item), item]));
  const keys = [...new Set([...baselineByKey.keys(), ...candidateByKey.keys(), ...expectedByKey.keys()])].sort();
  const cases = keys.map(key => {
    const baseline = baselineByKey.get(key);
    const candidate = candidateByKey.get(key);
    const exemplar = candidate ?? baseline ?? expectedByKey.get(key);
    const baselineStatus = statusOf(baseline);
    const candidateStatus = statusOf(candidate);
    return {
      routeId: exemplar.routeId, variantId: exemplar.variantId, caseId: exemplar.caseId, provider: exemplar.provider,
      sourceType: exemplar.sourceType, clientType: exemplar.clientType, cacheState: exemplar.cacheState,
      baselineStatus, candidateStatus, change: changeOf(baselineStatus, candidateStatus),
      baselineOutboundAttempts: baseline?.outboundAttempts ?? null,
      candidateOutboundAttempts: candidate?.outboundAttempts ?? null,
      outboundAttemptsDelta: baseline && candidate && baseline.outboundAttempts !== null && candidate.outboundAttempts !== null ? candidate.outboundAttempts - baseline.outboundAttempts : null,
      baselineCorrectImportedNeeds: correctImportedNeeds(baseline),
      candidateCorrectImportedNeeds: correctImportedNeeds(candidate),
      correctImportedNeedsDelta: baseline && candidate ? correctImportedNeeds(candidate) - correctImportedNeeds(baseline) : null,
      baselineUnexpectedAssignments: baseline?.unexpectedAssignments.length ?? null,
      candidateUnexpectedAssignments: candidate?.unexpectedAssignments.length ?? null,
      baselineExpectedReleaseIds: baseline?.expectedReleaseIds ?? null,
      candidateExpectedReleaseIds: candidate?.expectedReleaseIds ?? null,
      baselineReturnedReleaseIds: baseline?.returnedReleaseIds ?? null,
      candidateReturnedReleaseIds: candidate?.returnedReleaseIds ?? null,
      baselineSelectedReleaseIds: baseline?.selectedReleaseIds ?? null,
      candidateSelectedReleaseIds: candidate?.selectedReleaseIds ?? null,
      baselineReleaseRecall: baseline?.releaseMetrics?.recall ?? null,
      candidateReleaseRecall: candidate?.releaseMetrics?.recall ?? null,
      baselineReleasePrecision: baseline?.releaseMetrics?.precision ?? null,
      candidateReleasePrecision: candidate?.releaseMetrics?.precision ?? null,
      baselineElapsedMs: baseline?.elapsedMs ?? null,
      candidateElapsedMs: candidate?.elapsedMs ?? null,
      elapsedMsDelta: baseline && candidate && baseline.elapsedMs !== null && candidate.elapsedMs !== null ? candidate.elapsedMs - baseline.elapsedMs : null,
    };
  });
  const routesReport = routes.map(route => {
    const routeCases = cases.filter(item => item.routeId === route.id);
    const baselinePassed = routeCases.filter(item => item.baselineStatus === 'PASS').length;
    const candidatePassed = routeCases.filter(item => item.candidateStatus === 'PASS').length;
    const candidateRows = candidateRun.results.filter(item => item.routeId === route.id);
    const coveredVariants = new Set(candidateRows.map(item => item.variantId));
    const missingVariants = route.variants.filter(variant => !coveredVariants.has(variant));
    const missingActions = route.actions.filter(action => !candidateRows.some(item => item.actions.includes(action)));
    const missingJourneys = route.actions.flatMap(action => route.variants.map(variant => ({ action, variant })))
      .filter(journey => !candidateRows.some(item => item.variantId === journey.variant && item.actions.includes(journey.action)));
    const paired = routeCases.filter(item => item.outboundAttemptsDelta !== null);
    return {
      routeId: route.id, name: route.name, required: route.required,
      cases: routeCases.length, baselinePassed, candidatePassed,
      passedDelta: candidatePassed - baselinePassed,
      outboundAttemptsDelta: sum(paired, 'outboundAttemptsDelta'),
      correctImportedNeedsDelta: sum(paired, 'correctImportedNeedsDelta'),
      elapsedMsDelta: sum(paired, 'elapsedMsDelta'),
      completionRate: ratio(candidatePassed, routeCases.length), missingVariants, missingActions, missingJourneys,
    };
  });
  const assessments = cases.map(item => ({
    required: routeMap.get(item.routeId)?.required !== false,
    outcome: item.candidateStatus,
  }));
  for (const route of routes) {
    if (route.required && !cases.some(item => item.routeId === route.id)) assessments.push({ required: true, outcome: 'MISSING' });
    const candidateRows = candidateRun.results.filter(item => item.routeId === route.id);
    if (route.required && route.actions.some(action => route.variants.some(variant => !candidateRows.some(item => item.variantId === variant && item.actions.includes(action))))) {
      assessments.push({ required: true, outcome: 'MISSING' });
    }
  }
  for (const expected of candidateRun.manifest.expectedCases) {
    const required = ['routeId', 'variantId', 'caseId', 'provider', 'sourceType', 'clientType', 'cacheState'];
    if (required.some(field => typeof expected[field] !== 'string' || !expected[field])) throw new Error('expectedCases entries require full case dimensions');
    if (!candidateByKey.has(caseKey(expected))) assessments.push({ required: true, outcome: 'MISSING' });
  }
  return {
    manifest: { baseline: baselineRun.manifest, candidate: candidateRun.manifest },
    cases, routes: routesReport,
    strata: Object.fromEntries(['provider', 'sourceType', 'clientType', 'cacheState'].map(key => [key, aggregateRows(cases, key)])),
    totals: {
      cases: cases.length,
      baselinePassed: cases.filter(item => item.baselineStatus === 'PASS').length,
      candidatePassed: cases.filter(item => item.candidateStatus === 'PASS').length,
    },
    exitCode: enforceGates ? determineExitCode(assessments) : 0,
  };
}

export async function loadRun(directory) {
  const root = resolve(directory);
  const [manifest, results, evidenceLedger] = await Promise.all([
    readFile(resolve(root, 'manifest.json'), 'utf8').then(JSON.parse),
    readFile(resolve(directory, 'results.json'), 'utf8').then(JSON.parse),
    readFile(resolve(directory, 'evidence-ledger.json'), 'utf8').then(JSON.parse),
  ]);
  for (const [name, input] of [['corpus', manifest.corpus], ['sharedSettings', manifest.sharedSettings]]) {
    if (!input?.path || !input?.hash) throw new Error(`${name} identity is incomplete`);
    const path = resolve(root, input.path);
    if (path !== root && !path.startsWith(`${root}${sep}`)) throw new Error(`${name} path leaves the run directory`);
    const actual = createHash('sha256').update(await readFile(path)).digest('hex');
    const expected = input.hash.replace(/^sha256:/, '');
    if (actual !== expected) throw new Error(`${name} hash does not match ${input.path}`);
  }
  const resultRows = Array.isArray(results) ? results : results.results;
  if (!Array.isArray(resultRows)) throw new Error('results.json must contain an array');
  for (const entry of [...(evidenceLedger.files ?? []), ...(evidenceLedger.fixedInputs ?? []).map(item => ({ ...item, path: item.snapshot }))]) {
    if (!entry.path || !entry.sha256 || !Number.isInteger(entry.bytes)) throw new Error('evidence ledger entry is incomplete');
    const path = resolve(root, entry.path);
    if (path !== root && !path.startsWith(`${root}${sep}`)) throw new Error(`evidence ledger path leaves the run directory: ${entry.path}`);
    const bytes = await readFile(path);
    if (bytes.length !== entry.bytes || createHash('sha256').update(bytes).digest('hex') !== entry.sha256) throw new Error(`evidence ledger hash or size mismatch: ${entry.path}`);
  }
  for (const row of resultRows) {
    for (const evidence of row.evidencePaths ?? []) {
      const evidencePath = resolve(root, evidence.path);
      if (evidencePath !== root && !evidencePath.startsWith(`${root}${sep}`)) throw new Error(`evidence path leaves the run directory: ${evidence.path}`);
      try { await stat(evidencePath); } catch { throw new Error(`evidence path is missing: ${evidence.path}`); }
    }
  }
  return { manifest, results: resultRows };
}

export async function main(argv = process.argv.slice(2)) {
  if (argv[0] !== 'compare') throw new Error('usage: report.mjs compare --baseline DIR --candidate DIR [--no-gates]');
  const value = flag => {
    const index = argv.indexOf(flag);
    if (index < 0 || !argv[index + 1]) throw new Error(`${flag} is required`);
    return argv[index + 1];
  };
  const comparison = compareRuns(await loadRun(value('--baseline')), await loadRun(value('--candidate')), {
    enforceGates: gatesEnabled(argv),
  });
  process.stdout.write(`${JSON.stringify(comparison, null, 2)}\n`);
  return comparison.exitCode;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  main().then(code => { process.exitCode = code; }).catch(error => {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 2;
  });
}
