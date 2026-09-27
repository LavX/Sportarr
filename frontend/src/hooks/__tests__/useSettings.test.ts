import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, cleanup, renderHook, waitFor } from '@testing-library/react';

type SettingsHook = typeof import('../useSettings')['useSettings'];
type Call = { path: string; method: string; headers: Headers; body: string | null };
const apiKey = 'owned-settings-test-key';
const jsonResponse = (body: unknown, status = 200) => new Response(JSON.stringify(body), {
  status, headers: { 'Content-Type': 'application/json' },
});
let useSettings: SettingsHook;
let calls: Call[];
let violations: string[];
let current: Record<string, unknown>;
let getFailure: Error | undefined;
let putFailure: Error | undefined;
let getStatus: number;
let holdGet: Promise<void> | undefined;
let releaseGet: (() => void) | undefined;
let inFlight: Set<Promise<Response>>;
let ownedLoads: Array<() => boolean>;
let savedSportarr: PropertyDescriptor | undefined;

beforeEach(async () => {
  vi.resetModules();
  ({ useSettings } = await import('../useSettings'));
  calls = [];
  violations = [];
  current = { id: 1, hostSettings: '{"port":1867}', uiSettings: '{}', lastModified: '2024-01-01T00:00:00Z' };
  getFailure = undefined;
  putFailure = undefined;
  getStatus = 200;
  holdGet = undefined;
  releaseGet = undefined;
  inFlight = new Set();
  ownedLoads = [];
  savedSportarr = Object.getOwnPropertyDescriptor(window, 'Sportarr');
  Object.defineProperty(window, 'Sportarr', { value: { urlBase: '' }, configurable: true, writable: true });
  vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const task = (async () => {
      const path = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      const method = init?.method ?? 'GET';
      const headers = new Headers(init?.headers);
      calls.push({ path, method, headers, body: typeof init?.body === 'string' ? init.body : null });
      if (headers.get('Accept') !== 'application/json') violations.push('Missing JSON Accept header');
      if (path === '/initialize.json' && method === 'GET') return jsonResponse({ apiKey });
      if (path !== '/api/settings' || !['GET', 'PUT'].includes(method)) {
        violations.push(`Unexpected request ${method} ${path}`);
        throw new Error('Unexpected settings request');
      }
      if (headers.get('X-Api-Key') !== apiKey || init?.credentials !== 'include') {
        violations.push('Settings request did not preserve authentication');
      }
      if (method === 'GET') {
        if (holdGet) await holdGet;
        if (getFailure) throw getFailure;
        return jsonResponse(current, getStatus);
      }
      if (headers.get('Content-Type') !== 'application/json' || typeof init?.body !== 'string') {
        violations.push('PUT did not carry JSON');
        throw new Error('Expected settings JSON');
      }
      if (putFailure) throw putFailure;
      current = JSON.parse(init.body);
      return jsonResponse(current);
    })();
    inFlight.add(task);
    void task.then(() => inFlight.delete(task), () => inFlight.delete(task));
    return task;
  }));
});

afterEach(async () => {
  releaseGet?.();
  try {
    await act(async () => { await Promise.allSettled([...inFlight]); });
    await waitFor(() => expect(ownedLoads.every(loading => !loading())).toBe(true));
    expect(inFlight.size).toBe(0);
    expect(violations).toEqual([]);
  } finally {
    cleanup();
    vi.unstubAllGlobals();
    if (savedSportarr) Object.defineProperty(window, 'Sportarr', savedSportarr);
    else Reflect.deleteProperty(window, 'Sportarr');
    vi.restoreAllMocks();
  }
});

async function loaded(result: { current: [unknown, unknown, boolean] }) {
  await waitFor(() => expect(result.current[2]).toBe(false));
  expect(calls.map(call => `${call.method} ${call.path}`)).toEqual([
    'GET /initialize.json', 'GET /api/settings',
  ]);
}

function putCall() {
  expect(calls.map(call => `${call.method} ${call.path}`)).toEqual([
    'GET /initialize.json', 'GET /api/settings', 'GET /api/settings', 'PUT /api/settings',
  ]);
  return calls[3];
}

describe('useSettings', () => {
  it('should initialize with default value', async () => {
    current.hostSettings = '{"port":8080}';
    const { result } = renderHook(() => useSettings('hostSettings', { port: 1867 }));
    ownedLoads.push(() => result.current[2]);
    expect(result.current[0]).toEqual({ port: 1867 });
    expect(result.current[2]).toBe(true);
    await loaded(result);
    expect(result.current[0]).toEqual({ port: 8080 });
  });

  it('should fetch settings on mount', async () => {
    current.hostSettings = '{"bindAddress":"*","port":1867,"instanceName":"Sportarr"}';
    const { result } = renderHook(() => useSettings('hostSettings', { port: 8080 }));
    ownedLoads.push(() => result.current[2]);
    await loaded(result);
    expect(result.current[0]).toEqual({ bindAddress: '*', port: 1867, instanceName: 'Sportarr' });
  });

  it('should handle fetch error gracefully', async () => {
    const error = new Error('Network error');
    getFailure = error;
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const { result } = renderHook(() => useSettings('hostSettings', { port: 1867 }));
    ownedLoads.push(() => result.current[2]);
    await loaded(result);
    expect(result.current[0]).toEqual({ port: 1867 });
    expect(consoleSpy).toHaveBeenCalledWith('Failed to fetch hostSettings:', error);
  });

  it('should save settings correctly', async () => {
    const { result } = renderHook(() => useSettings('hostSettings', { port: 1867, bindAddress: '*' }));
    ownedLoads.push(() => result.current[2]);
    await loaded(result);
    await act(async () => { await result.current[1]({ port: 8080, bindAddress: '0.0.0.0' }); });
    const sent = JSON.parse(putCall().body!);
    expect(sent.hostSettings).toBe('{"port":8080,"bindAddress":"0.0.0.0"}');
    expect(sent.id).toBe(1);
    expect(sent.uiSettings).toBe('{}');
    expect(result.current[0]).toEqual({ port: 8080, bindAddress: '0.0.0.0' });
  });

  it('should handle save error', async () => {
    const error = new Error('Save failed');
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const { result } = renderHook(() => useSettings('hostSettings', { port: 1867 }));
    ownedLoads.push(() => result.current[2]);
    await loaded(result);
    putFailure = error;
    await act(async () => { await expect(result.current[1]({ port: 8080 })).rejects.toBe(error); });
    expect(JSON.parse(putCall().body!).hostSettings).toBe('{"port":8080}');
    expect(result.current[0]).toEqual({ port: 1867 });
    expect(consoleSpy).toHaveBeenCalledWith('Failed to save hostSettings:', error);
  });

  it('should handle malformed JSON gracefully', async () => {
    current.hostSettings = 'invalid json{';
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const { result } = renderHook(() => useSettings('hostSettings', { port: 1867 }));
    ownedLoads.push(() => result.current[2]);
    await loaded(result);
    expect(result.current[0]).toEqual({ port: 1867 });
    expect(consoleSpy).toHaveBeenCalledWith('Failed to fetch hostSettings:', expect.any(SyntaxError));
  });

  it('should handle 404 response', async () => {
    getStatus = 404;
    const { result } = renderHook(() => useSettings('hostSettings', { port: 1867 }));
    ownedLoads.push(() => result.current[2]);
    await loaded(result);
    expect(result.current[0]).toEqual({ port: 1867 });
  });

  it('should update loading state correctly', async () => {
    holdGet = new Promise<void>(resolve => { releaseGet = resolve; });
    current.hostSettings = '{"port":8080}';
    const { result } = renderHook(() => useSettings('hostSettings', { port: 1867 }));
    ownedLoads.push(() => result.current[2]);
    try {
      await waitFor(() => expect(calls.some(call => call.path === '/api/settings')).toBe(true));
      expect(result.current[2]).toBe(true);
      expect(result.current[0]).toEqual({ port: 1867 });
    } finally {
      await act(async () => { releaseGet!(); await Promise.allSettled([...inFlight]); });
    }
    await loaded(result);
    expect(result.current[0]).toEqual({ port: 8080 });
  });

  it('should preserve other settings when saving', async () => {
    current.uiSettings = '{"theme":"dark"}';
    current.securitySettings = '{"apiKey":"owned-fixture-secret"}';
    const { result } = renderHook(() => useSettings('hostSettings', { port: 1867 }));
    ownedLoads.push(() => result.current[2]);
    await loaded(result);
    current.uiSettings = '{"theme":"light"}';
    await act(async () => { await result.current[1]({ port: 8080 }); });
    const sent = JSON.parse(putCall().body!);
    expect(sent.hostSettings).toBe('{"port":8080}');
    expect(sent.uiSettings).toBe('{"theme":"light"}');
    expect(sent.securitySettings).toBe('{"apiKey":"owned-fixture-secret"}');
    expect(result.current[0]).toEqual({ port: 8080 });
  });

  it('should handle empty settings key', async () => {
    current.hostSettings = '';
    const { result } = renderHook(() => useSettings('hostSettings', { port: 1867 }));
    ownedLoads.push(() => result.current[2]);
    await loaded(result);
    expect(result.current[0]).toEqual({ port: 1867 });
  });
});
