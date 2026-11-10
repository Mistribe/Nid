import { useTranslation } from 'react-i18next';
import type { Projection } from '@/api/dashboard';
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from '@/components/ui/tooltip';
import { yyyyMmLabel } from '@/lib/format';
import '@/locales/scenarios';

/** The date of financial independence: its month, « Non atteinte avant l'horizon », or « — » (with why) when no living cost is set. */
export function Independence({ projection }: { projection: Projection }) {
  const { t } = useTranslation();
  if (projection.financialIndependence) return <>{yyyyMmLabel(projection.financialIndependence)}</>;
  if (Number(projection.assumptions.monthlyLivingCost) > 0) return <>{t('scenarios.projection.independence.unreached')}</>;
  const why = t('scenarios.projection.independence.living_cost_missing');
  return (
    <TooltipProvider>
      <Tooltip>
        <TooltipTrigger type="button" aria-label={why} className="rounded outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50">{t('scenarios.dash')}</TooltipTrigger>
        <TooltipContent>{why}</TooltipContent>
      </Tooltip>
    </TooltipProvider>
  );
}
