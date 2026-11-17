import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import type { TaxEstimate } from '@/api/tax';
import { EmptyState } from '@/components/EmptyState';
import { Button, buttonVariants } from '@/components/ui/button';
import { ZERO } from '../format';
import { TaxNotices } from '../TaxNotices';
import type { TaxTab } from '../TaxPage';
import { IncomeSplit } from './IncomeSplit';
import { Pistes } from './Pistes';
import { TaxHero } from './TaxHero';
import '@/locales/tax';

/** The hero says these; the notes would repeat them (Pistes says the PER's). */
const SAID_ELSEWHERE = ['rules_carried_forward', 'per_ceiling_estimated'];

/** The page's lead: the year's tax, where the income goes, what to know and what to do about it. */
export function OverviewTab({ estimate, onTab }: { estimate: TaxEstimate; onTab: (tab: TaxTab) => void }) {
  const { t } = useTranslation();
  const r = estimate.result;
  if (ZERO.test(r.netBeforeTax)) {
    return (
      <EmptyState module="/tax" title={t('tax.overview.empty_title', { year: estimate.year })} body={t('tax.overview.empty_body')}
        action={(
          <>
            <Link to="/income/new" className={buttonVariants({ variant: 'outline' })}>{t('tax.overview.empty_income')}</Link>
            <Button onClick={() => onTab('profile')}>{t('tax.overview.empty_profile')}</Button>
          </>
        )} />
    );
  }
  return (
    <>
      <p className="text-sm text-muted-foreground">{t('tax.overview.question')}</p>
      <TaxHero estimate={estimate} onTab={onTab} />
      <IncomeSplit result={r} />
      <div className="grid gap-4 lg:grid-flow-col lg:auto-cols-fr">
        <TaxNotices notices={estimate.notices} warnings={[...new Set(r.warnings)].filter((w) => !SAID_ELSEWHERE.includes(w))} year={estimate.year} rulesYear={r.rulesYear} />
        <Pistes estimate={estimate} />
      </div>
    </>
  );
}
