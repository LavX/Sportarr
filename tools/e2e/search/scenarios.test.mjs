import test from 'node:test';
import assert from 'node:assert/strict';
import { getScenario, scenarioNames } from './scenarios.mjs';
import routes from './routes.json' with { type: 'json' };

test('every executable scenario names a real route action and variant', () => {
  for (const name of scenarioNames) {
    const scenario = getScenario(name);
    const route = routes.find(route => route.id === scenario.routeId);
    assert.ok(route, name);
    assert.ok(route.variants.includes(scenario.variantId), name);
    assert.ok(scenario.actions.every(action => route.actions.includes(action)), name);
  }
  assert.throws(() => getScenario('not-a-route'));
});

test('direct and scheduled RSS task requests retain distinct API routes', () => {
  assert.deepEqual(getScenario('rss-task-direct').request({}), {
    path: '/api/task', body: { name: 'Fixture RSS Sync', commandName: 'RssSync' },
  });
  assert.equal(getScenario('rss-task').request({}).path, '/api/task/scheduled/rss-sync/trigger');
  assert.deepEqual(getScenario('rss-task-direct').actions, ['POST /api/task with RssSync']);
});

test('explicit event task carries its event and part in the string body', () => {
  const request = getScenario('event-task').request({ eventId: 42 });
  assert.equal(request.path, '/api/task');
  assert.equal(request.body.commandName, 'EventSearch');
  assert.equal(request.body.body, '42|Main Card');
});
