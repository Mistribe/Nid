import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { useSession } from '@/app/Providers';
import { formatMoney } from '@/components/Money';
import { OwnerChips } from '@/components/OwnerChips';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Skeleton } from '@/components/ui/skeleton';
import { OVERVIEW } from '@/api/income';
import type { DashboardView } from '@/api/dashboard';
import { Value } from '../KeyFigures';

/** Spec §4.2: the sources and rents behind « Revenus nets / mois », each with its monthly equivalent and owners. */
export function IncomePanel({ d, scope }: { d: DashboardView; scope: 'me' | 'household' }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { capabilities } = useSession();
  const sources = $api.useQuery('get', OVERVIEW, { params: { query: { scope } } }, { enabled: capabilities.modules.includes('income') });
  const running = sources.data?.sources.filter((s) => !s.ended);
  return (
    <div className="space-y-4">
      <div className="space-y-1">
        <Value figure={d.income} unknown={t('dashboard.income_unknown')} />
        <p className="text-sm text-muted-foreground">{t('dashboard.smoothed_per_month')}</p>
      </div>
      {sources.error ? <ProblemMessage problem={present(toProblem(sources.error))} /> : null}
      {!running && !sources.error && capabilities.modules.includes('income') ? <Skeleton className="h-16 w-full" /> : null}
      {running ? (
        <ul className="divide-y">
          {running.map((s) => (
            <li key={s.id} className="flex flex-wrap items-center justify-between gap-2 py-2">
              {s.module === 'realestate'
                ? <Link to="/properties/$id" params={{ id: s.id }} search={{ tab: 'rental' }} className="underline">{s.name}</Link>
                : <Link to="/income/$id" params={{ id: s.id }} className="underline">{s.name}</Link>}
              <OwnerChips owners={s.owners} />
              <span className="tabular-nums">{s.perMonth ? formatMoney(s.perMonth) : '—'}</span>
            </li>
          ))}
        </ul>
      ) : null}
      {capabilities.modules.includes('income') ? <Link to="/income" className="text-sm underline">{t('dashboard.see_income')}</Link> : null}
    </div>
  );
}
