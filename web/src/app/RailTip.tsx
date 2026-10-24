import type { ReactElement, ReactNode } from 'react';
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from '@/components/ui/tooltip';

/** A rail entry's name (and count) beside its icon, on hover or focus. Lazy: Radix stays out of the initial bundle. */
export function RailTip({ content, children }: { content: ReactNode; children: ReactElement }) {
  return (
    <TooltipProvider>
      <Tooltip>
        <TooltipTrigger asChild>{children}</TooltipTrigger>
        <TooltipContent side="right" sideOffset={8} className="flex items-center gap-2">{content}</TooltipContent>
      </Tooltip>
    </TooltipProvider>
  );
}
