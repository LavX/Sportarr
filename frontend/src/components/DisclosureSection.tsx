import type { ReactNode } from 'react';
import { ChevronDownIcon } from '@heroicons/react/24/outline';

interface DisclosureSectionProps {
  title: string;
  description?: string;
  children: ReactNode;
  defaultOpen?: boolean;
  className?: string;
}

export default function DisclosureSection({
  title,
  description,
  children,
  defaultOpen = false,
  className = '',
}: DisclosureSectionProps) {
  return (
    <details
      open={defaultOpen || undefined}
      className={`group overflow-hidden rounded-lg border border-gray-800 bg-gray-900/70 ${className}`.trim()}
    >
      <summary className="flex min-h-14 cursor-pointer list-none items-center justify-between gap-4 px-4 py-3 text-left transition-colors hover:bg-gray-800/50 [&::-webkit-details-marker]:hidden">
        <span className="min-w-0">
          <span className="block font-medium text-white">{title}</span>
          {description && <span className="mt-0.5 block text-sm text-gray-400">{description}</span>}
        </span>
        <ChevronDownIcon className="h-5 w-5 shrink-0 text-gray-400 transition-transform group-open:rotate-180" />
      </summary>
      <div className="border-t border-gray-800 p-4 sm:p-6">{children}</div>
    </details>
  );
}
