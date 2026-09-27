export interface TvGuideMobileControlsProps {
  dateLabel: string;
  syncing: boolean;
  filtersOpen: boolean;
  onSync: () => void;
  onOptions: () => void;
  onPrevious: () => void;
  onNow: () => void;
  onNext: () => void;
  onFilters: () => void;
}

export default function TvGuideMobileControls({
  dateLabel,
  syncing,
  filtersOpen,
  onSync,
  onOptions,
  onPrevious,
  onNow,
  onNext,
  onFilters,
}: TvGuideMobileControlsProps) {
  return (
    <div className="space-y-3">
      <div role="group" aria-label="Guide actions" className="grid grid-cols-2 gap-2">
        <button
          type="button"
          onClick={onSync}
          disabled={syncing}
          className={`${BUTTON_SECONDARY} min-h-11 px-3`}
        >
          <ArrowPathIcon className={`h-5 w-5 ${syncing ? 'animate-spin' : ''}`} />
          Sync EPG
        </button>
        <button
          type="button"
          onClick={onOptions}
          className={`${BUTTON_SECONDARY} min-h-11 px-3`}
          aria-label="IPTV Options"
        >
          <Cog6ToothIcon className="h-5 w-5" />
          Options
        </button>
      </div>

      <div className="rounded-lg border border-gray-800 bg-black/30 p-3">
        <p className="mb-3 text-center text-sm font-medium text-gray-300">{dateLabel}</p>
        <div
          role="group"
          aria-label="Guide time controls"
          className="grid grid-cols-[2.75rem_1fr_2.75rem_1fr] gap-2"
        >
          <button
            type="button"
            onClick={onPrevious}
            className={`${BUTTON_ICON_SECONDARY} min-h-11 min-w-11`}
            aria-label="Previous 6 hours"
          >
            <ChevronLeftIcon className="h-5 w-5" />
          </button>
          <button type="button" onClick={onNow} className={`${BUTTON_SECONDARY} min-h-11 px-3`}>
            Now
          </button>
          <button
            type="button"
            onClick={onNext}
            className={`${BUTTON_ICON_SECONDARY} min-h-11 min-w-11`}
            aria-label="Next 6 hours"
          >
            <ChevronRightIcon className="h-5 w-5" />
          </button>
          <button
            type="button"
            onClick={onFilters}
            className={`${filtersOpen ? BUTTON_PRIMARY : BUTTON_SECONDARY} min-h-11 px-3`}
            aria-expanded={filtersOpen}
            aria-controls="tv-guide-filters"
          >
            <FunnelIcon className="h-5 w-5" />
            Filters
          </button>
        </div>
      </div>
    </div>
  );
}
import {
  ArrowPathIcon,
  ChevronLeftIcon,
  ChevronRightIcon,
  Cog6ToothIcon,
  FunnelIcon,
} from '@heroicons/react/24/outline';
import {
  BUTTON_ICON_SECONDARY,
  BUTTON_PRIMARY,
  BUTTON_SECONDARY,
} from '../utils/designTokens';
