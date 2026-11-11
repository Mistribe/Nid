import { Info } from 'lucide-react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from '@/components/ui/tooltip';

/** An « i » beside a label that explains it: opens on hover and on keyboard focus, read out through aria-describedby once open. */
export function InfoTip({ label, children }: { label: string; children: ReactNode }) {
  const { t } = useTranslation();
  return (
    <TooltipProvider>
      <Tooltip>
        <TooltipTrigger type="button" aria-label={t('common.about', { name: label })}
          className="inline-flex shrink-0 rounded-full text-muted-foreground outline-none hover:text-foreground focus-visible:ring-[3px] focus-visible:ring-ring/50">
          <Info aria-hidden="true" className="size-3.5" />
        </TooltipTrigger>
        <TooltipContent className="max-w-72 text-pretty">{children}</TooltipContent>
      </Tooltip>
    </TooltipProvider>
  );
}
