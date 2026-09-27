export type RecordingStatus =
  | 'Scheduled'
  | 'Recording'
  | 'Completed'
  | 'Failed'
  | 'Cancelled'
  | 'Importing'
  | 'Imported';

export type RecordingView = 'all' | 'upcoming' | 'recording' | 'completed' | 'attention';

export { getVisibleSelectedIds } from './selectionPresentation';

export function getRecordingAttentionCount(stats: {
  failedCount: number;
  cancelledCount: number;
}): number {
  return stats.failedCount + stats.cancelledCount;
}

export function getRecordingCompletedCount(stats: {
  completedCount: number;
  importedCount: number;
  importingCount: number;
}): number {
  return stats.completedCount + stats.importedCount + stats.importingCount;
}

export function recordingMatchesView(status: RecordingStatus, view: RecordingView): boolean {
  if (view === 'all') return true;
  if (view === 'upcoming') return status === 'Scheduled';
  if (view === 'recording') return status === 'Recording';
  if (view === 'completed') return status === 'Completed' || status === 'Imported' || status === 'Importing';
  return status === 'Failed' || status === 'Cancelled';
}
