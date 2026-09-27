import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { mkdir, writeFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';

export function validateJourney(input) {
  assert.ok(Number.isSafeInteger(input.eventId) && input.eventId > 0);
  assert.ok(Number.isSafeInteger(input.leagueId) && input.leagueId > 0);
  assert.equal(input.title, 'UFC.9999.2026.09.01.Main.Card.720p.WEB-DL.H264-SEARCHFIXTURE');
  assert.ok(['browser-manual', 'browser-automatic'].includes(input.scenario));
  assert.match(input.apiKey, /^[a-f0-9]{48}$/);
}

export async function runJourney(input) {
  validateJourney(input);
  const { chromium } = await import('/work/node_modules/playwright/index.mjs');
  const origin = 'http://sv-b0-app:1867';
  await mkdir('/case', { recursive: true });
  const browser = await chromium.launch({ headless: true });
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, colorScheme: 'dark', extraHTTPHeaders: { 'X-Api-Key': input.apiKey } });
  await context.route('**/*', route => new URL(route.request().url()).origin === origin ? route.continue() : route.abort());
  await context.tracing.start({ screenshots: true, snapshots: true, sources: true });
  const page = await context.newPage();
  const requests = [];
  page.on('request', request => {
    if (request.method() === 'POST') requests.push({ path: new URL(request.url()).pathname, body: request.postDataJSON() });
  });
  try {
    await page.goto(`${origin}/leagues/${input.leagueId}`, { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1500);
    const skip = page.getByRole('button', { name: 'Skip all steps', exact: true });
    if (await skip.isVisible()) await skip.click();
    const title = input.scenario === 'browser-manual' ? 'Manual Search' : 'Auto Search';
    const selector = `button[title*="${title}"][title*="Main Card"]:visible` + (input.scenario === 'browser-automatic' ? ',button[title="Search for monitored Main Card"]:visible' : '');
    const button = page.locator(selector);
    if (await button.count() === 0) {
      const season = page.getByRole('button', { name: /^Season 2026/ });
      if (await season.isVisible()) await season.click();
    }
    if (input.scenario === 'browser-manual') {
      const searchResponse = page.waitForResponse(response => response.request().method() === 'POST' && response.url() === `${origin}/api/event/${input.eventId}/search`);
      await button.first().click({ timeout: 20000 });
      assert.ok((await searchResponse).ok());
      const row = page.locator('tr:visible').filter({ hasText: input.title });
      await row.waitFor({ state: 'visible', timeout: 20000 });
      await page.screenshot({ path: '/case/results.png', fullPage: true });
      const grabResponse = page.waitForResponse(response => response.request().method() === 'POST' && response.url() === `${origin}/api/release/grab`);
      await row.locator('button[title="Download"]:visible').click();
      const response = await grabResponse;
      assert.ok(response.ok(), 'the browser grab request must succeed');
      const result = await response.json();
      await writeFile('/case/result.json', JSON.stringify({ requests, result }, null, 2));
      return result;
    }
    const searchResponse = page.waitForResponse(response => response.request().method() === 'POST' && response.url() === `${origin}/api/event/${input.eventId}/automatic-search`);
    await button.first().click({ timeout: 20000 });
    const response = await searchResponse;
    assert.ok(response.ok());
    const result = await response.json();
    await page.screenshot({ path: '/case/automatic.png', fullPage: true });
    await writeFile('/case/result.json', JSON.stringify({ requests, result }, null, 2));
    return result;
  } finally {
    await page.screenshot({ path: '/case/final.png', fullPage: true }).catch(() => {});
    await writeFile('/case/requests.json', JSON.stringify(requests, null, 2));
    await context.tracing.stop({ path: '/case/trace.zip' });
    await browser.close();
  }
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  let used = false;
  createServer(async (request, response) => {
    try {
      assert.equal(request.method, 'POST');
      assert.equal(request.url, '/journey');
      assert.equal(used, false, 'each browser instance runs one journey');
      let body = '';
      for await (const chunk of request) { body += chunk; assert.ok(body.length < 8192); }
      const input = JSON.parse(body);
      validateJourney(input);
      used = true;
      const result = await runJourney(input);
      response.setHeader('Content-Type', 'application/json');
      response.end(JSON.stringify(result));
    } catch (error) { response.statusCode = 500; response.end(error.stack); }
  }).listen(9082, '0.0.0.0');
}
