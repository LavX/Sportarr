export function getPhoneGuideReferenceTime(
  currentTime: Date,
  guideStart: Date,
  guideEnd: Date,
  timeOffsetHours: number = 0
): number {
  const currentMs = currentTime.getTime();
  const guideStartMs = guideStart.getTime();
  const guideEndMs = guideEnd.getTime();

  if (timeOffsetHours === 0 && currentMs >= guideStartMs && currentMs < guideEndMs) return currentMs;
  return guideStartMs;
}
