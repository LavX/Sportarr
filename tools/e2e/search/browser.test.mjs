import test from 'node:test';
import assert from 'node:assert/strict';
import { validateJourney } from './browser.mjs';

test('browser actor accepts only bounded synthetic journeys', () => {
  const valid = { eventId: 1, leagueId: 1, scenario: 'browser-manual', apiKey: 'a'.repeat(48), title: 'UFC.9999.2026.09.01.Main.Card.720p.WEB-DL.H264-SEARCHFIXTURE' };
  assert.doesNotThrow(() => validateJourney(valid));
  assert.throws(() => validateJourney({ ...valid, scenario: 'arbitrary' }));
  assert.throws(() => validateJourney({ ...valid, eventId: '../another' }));
  assert.throws(() => validateJourney({ ...valid, apiKey: undefined }));
});
