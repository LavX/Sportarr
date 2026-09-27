import { buildNativePushTrigger, buildCompatiblePushTrigger, buildAllAutomaticSearchTrigger } from './push-scenarios.mjs';
const scenarios = {
  'native-push': { routeId: 'R18', variantId: 'accepted', actions: ['POST /api/release/push'], expectedPart: 'Main Card', push: true, request: input => buildNativePushTrigger(input).request },
  'compatible-push': { routeId: 'R19', variantId: 'response contract', actions: ['POST /api/v3/release/push'], expectedPart: 'Main Card', push: true, request: input => buildCompatiblePushTrigger(input).request },
  'all-auto': { routeId: 'R08', variantId: 'single league', actions: ['POST /api/automatic-search/all'], expectedPart: 'Main Card', request: input => buildAllAutomaticSearchTrigger(input).request },
  'browser-manual': { routeId: 'R01', variantId: 'page and modal entry points', actions: ['POST /api/event/{id}/search', 'POST /api/release/grab'], browser: true, expectedPart: 'Main Card' },
  'browser-automatic': { routeId: 'R05', variantId: 'one part', actions: ['POST /api/event/{id}/automatic-search'], browser: true, expectedPart: 'Main Card' },
  manual: { routeId: 'R01', variantId: 'ordinary search', actions: ['POST /api/event/{id}/search', 'POST /api/release/grab'],
    interactive: true, expectedPart: 'Main Card', request: ({ eventId }) => ({ path: `/api/event/${eventId}/search`, body: {} }) },
  'manual-warm': { routeId: 'R01', variantId: 'ordinary search', actions: ['POST /api/event/{id}/search', 'POST /api/release/grab'],
    interactive: true, expectedPart: 'Main Card', request: ({ eventId }) => ({ path: `/api/event/${eventId}/search`, body: {} }) },
  'release-search': { routeId: 'R02', variantId: 'API path', actions: ['POST /api/release/search', 'POST /api/release/grab'],
    interactive: true, expectedPart: 'Main Card', request: () => ({ path: '/api/release/search', body: { query: 'UFC 9999', maxResultsPerIndexer: 100 } }) },
  automatic: { routeId: 'R05', variantId: 'one part', actions: ['POST /api/event/{id}/automatic-search', 'EventSearch task'],
    expectedPart: 'Main Card', request: ({ eventId }) => ({ path: `/api/event/${eventId}/automatic-search`, body: { part: 'Main Card' } }) },
  'league-auto': { routeId: 'R06', variantId: 'completed', actions: ['POST /api/league/{id}/automatic-search'],
    expectedPart: 'Main Card', request: ({ leagueId }) => ({ path: `/api/league/${leagueId}/automatic-search`, body: {} }) },
  'season-auto': { routeId: 'R07', variantId: 'queueing', actions: ['POST /api/leagues/{id}/seasons/{season}/automatic-search'],
    expectedPart: 'Main Card', request: ({ leagueId }) => ({ path: `/api/leagues/${leagueId}/seasons/2026/automatic-search`, body: {} }) },
  queue: { routeId: 'R09', variantId: 'task completion', actions: ['POST /api/search/queue'],
    expectedPart: 'Main Card', request: ({ eventId }) => ({ path: '/api/search/queue', body: { eventId, part: 'Main Card' } }) },
  'wanted-missing': { routeId: 'R10', variantId: 'repeat click', actions: ['POST /api/wanted/missing/search-all'],
    expectedPart: 'Main Card', repeatTrigger: true, request: () => ({ path: '/api/wanted/missing/search-all', body: {} }) },
  'search-on-add': { routeId: 'R13', variantId: 'enabled', actions: ['POST /api/events with SearchOnAdd'], expectedPart: 'Main Card' },
  'rss-background': { routeId: 'R15', variantId: 'first poll', actions: ['RssSyncService loop'], expectedPart: 'Main Card', rss: true },
  'rss-task': { routeId: 'R16', variantId: 'actual requests', actions: ['POST /api/task/scheduled/rss-sync/trigger'],
    expectedPart: 'Main Card', rss: true, task: 'RssSync', request: () => ({ path: '/api/task/scheduled/rss-sync/trigger', body: {} }) },
  'rss-task-direct': { routeId: 'R16', variantId: 'actual requests', actions: ['POST /api/task with RssSync'],
    expectedPart: 'Main Card', rss: true, task: 'RssSync', request: () => ({ path: '/api/task', body: { name: 'Fixture RSS Sync', commandName: 'RssSync' } }) },
  'event-task': { routeId: 'R17', variantId: 'task body', actions: ['POST /api/task with EventSearch'], expectedPart: 'Main Card',
    request: ({ eventId }) => ({ path: '/api/task', body: { name: 'Fixture Event Search', commandName: 'EventSearch', body: `${eventId}|Main Card` } }) },
};

export const scenarioNames = Object.freeze(Object.keys(scenarios));
export function getScenario(name) {
  if (!Object.hasOwn(scenarios, name)) throw new Error(`Unknown scenario: ${name}`);
  return scenarios[name];
}
