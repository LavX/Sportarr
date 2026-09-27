import { describe, expect, it } from 'vitest';
import {
  detectStreamType,
  getFfmpegStartPath,
  getHlsPlaybackConfig,
  getAutomaticPlaybackRecovery,
  isPlaybackGenerationCurrent,
} from './streamPlaybackConfig';

describe('detectStreamType', () => {
  it('uses HLS playback for extensionless provider streams', () => {
    expect(detectStreamType('https://provider.example/live/user/token/200163456'))
      .toBe('hls');
  });

  it('builds explicit FFmpeg normalization requests', () => {
    expect(getFfmpegStartPath(24, false)).toBe('/v1/stream/24/start?normalize=false');
    expect(getFfmpegStartPath(24, true)).toBe('/v1/stream/24/start?normalize=true');
  });

  it('exposes bounded live playback profiles for runtime tuning', () => {
    const lowLatency = getHlsPlaybackConfig('low-latency');
    expect(lowLatency).toMatchObject({
      lowLatencyMode: true,
      liveSyncDuration: 3,
      liveMaxLatencyDuration: 10,
      maxBufferLength: 12,
      maxMaxBufferLength: 30,
    });
    expect(lowLatency.fragLoadPolicy.default.errorRetry).toMatchObject({
      maxNumRetry: 4,
      retryDelayMs: 750,
    });
    expect(getHlsPlaybackConfig('balanced')).toMatchObject({
      lowLatencyMode: false,
      liveSyncDuration: 6,
      liveMaxLatencyDuration: 20,
      maxBufferLength: 30,
      maxMaxBufferLength: 120,
    });
    expect(getHlsPlaybackConfig('resilient')).toMatchObject({
      liveSyncDuration: 12,
      liveMaxLatencyDuration: 40,
      maxBufferLength: 60,
      maxMaxBufferLength: 180,
    });
  });

  it('returns independent retry policies for each player instance', () => {
    const first = getHlsPlaybackConfig('balanced');
    const second = getHlsPlaybackConfig('balanced');

    first.fragLoadPolicy.default.errorRetry.maxNumRetry = 99;

    expect(second.fragLoadPolicy.default.errorRetry.maxNumRetry).toBe(6);
  });

  it('rejects stale playback completions after cleanup or a newer generation', () => {
    expect(isPlaybackGenerationCurrent(4, 4, false)).toBe(true);
    expect(isPlaybackGenerationCurrent(4, 5, false)).toBe(false);
    expect(isPlaybackGenerationCurrent(4, 4, true)).toBe(false);
  });

  it('uses one bounded stream-copy fallback without enabling normalization', () => {
    expect(getAutomaticPlaybackRecovery('proxy', 0)).toEqual({
      mode: 'ffmpeg',
      normalize: false,
    });
    expect(getAutomaticPlaybackRecovery('proxy', 1)).toBeNull();
    expect(getAutomaticPlaybackRecovery('ffmpeg', 0)).toBeNull();
    expect(getAutomaticPlaybackRecovery('direct', 0)).toBeNull();
  });
});
