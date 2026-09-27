import { describe, expect, it } from 'vitest';
import { getPhoneGuideReferenceTime } from '../tvGuidePresentation';

describe('getPhoneGuideReferenceTime', () => {
  it('uses the selected window start when browsing a past guide window', () => {
    const currentTime = new Date('2026-09-22T18:00:00Z');
    const guideStart = new Date('2026-09-22T00:00:00Z');
    const guideEnd = new Date('2026-09-22T12:00:00Z');

    expect(getPhoneGuideReferenceTime(currentTime, guideStart, guideEnd)).toBe(guideStart.getTime());
  });

  it('uses the real current time when it falls inside the guide window', () => {
    const currentTime = new Date('2026-09-22T06:00:00Z');
    const guideStart = new Date('2026-09-22T00:00:00Z');
    const guideEnd = new Date('2026-09-22T12:00:00Z');

    expect(getPhoneGuideReferenceTime(currentTime, guideStart, guideEnd)).toBe(currentTime.getTime());
  });

  it('uses the selected window start after one overlapping navigation step', () => {
    const currentTime = new Date('2026-09-22T06:00:00Z');
    const guideStart = new Date('2026-09-22T00:00:00Z');
    const guideEnd = new Date('2026-09-22T12:00:00Z');

    expect(getPhoneGuideReferenceTime(currentTime, guideStart, guideEnd, -6)).toBe(guideStart.getTime());
  });
});
