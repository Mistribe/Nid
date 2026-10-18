import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { Archive } from 'lucide-react';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { InvestmentsSearch, RootSearch } from '@/app/router';
import { EmptyState } from '@/components/EmptyState';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { OwnerChips } from '@/components/OwnerChips';
import { ProblemMessage } from '@/components/ProblemMessage';
import { ENVELOPES, type EnvelopeSummary } from './api';
import { kindText } from './format';
import { Gain, StaleBadge } from './Figures';

function useEnvelopes(scope: 'me' | 'household', closed: boolean) {
  const { $api } = useApi();
  return $api.useQuery('get', ENVELOPES, { params: { query: { scope, ...(closed ? { archived: true } : {}) } } });
}

/** The list of envelopes with their value, gain and staleness (2B2a design). */
export function EnvelopesPage() {
  const { t } = useTranslation();
  const search: RootSearch & InvestmentsSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const closedId = useId();
  const scope = search.scope === 'me' ? 'me' : 'household';
  const closed = search.closed === 1;
  const answer = useEnvelopes(scope, closed);
  const setClosed = (checked: boolean) =>
    void navigate({ to: '/investments', search: (prev: RootSearch & InvestmentsSearch) => ({ ...prev, closed: checked ? (1 as const) : undefined }), replace: true });

  const header = (
    <div className="flex flex-wrap items-center justify-between gap-3">
      <h1 className="text-2xl font-semibold">{t('investments.title')}</h1>
      <Link to="/investments/new" className="rounded-md bg-primary px-3 py-2 text-sm text-primary-foreground">{t('investments.add')}</Link>
    </div>
  );
  const showClosed = (
    <div className="flex items-center gap-2 text-sm">
      <input id={closedId} type="checkbox" checked={closed} onChange={(e) => setClosed(e.target.checked)} />
      <label htmlFor={closedId}>{t('investments.show_closed')}</label>
    </div>
  );

  // One stable frame whatever the answer: the checkbox must survive the reload its own change causes (focus).
  return (
    <div className="space-y-4">
      {header}
      {showClosed}
      <Body answer={answer} />
    </div>
  );
}

function Body({ answer }: { answer: ReturnType<typeof useEnvelopes> }) {
  const { t } = useTranslation();
  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;

  if (!answer.data)
    return (
      <ul aria-busy="true" aria-label={t('investments.loading')} className="space-y-2">
        {[0, 1, 2].map((i) => <li key={i} className="h-16 animate-pulse rounded-md border bg-muted" />)}
      </ul>
    );

  const data = answer.data;
  if (data.items.length === 0)
    return (
      <EmptyState title={t('investments.empty_title')} body={t('investments.empty_body')}
        action={<Link to="/investments/new" className="rounded-md bg-primary px-3 py-2 text-primary-foreground">{t('investments.add')}</Link>} />
    );

  return (
    <>
      <p className="text-sm text-muted-foreground">{t('investments.before_tax')}</p>
      <ul aria-label={t('investments.list')} className="space-y-2">
        {data.items.map((item) => <Row key={item.id} item={item} />)}
      </ul>
    </>
  );
}

function Row({ item }: { item: EnvelopeSummary }) {
  const { t } = useTranslation();
  return (
    <li className="space-y-1 rounded-md border p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <Link to="/investments/$id" params={{ id: item.id }} className="font-medium hover:underline">{item.displayName}</Link>
        <span className="flex items-center gap-2 text-sm text-muted-foreground">
          {item.closed ? <span className="flex items-center gap-1"><Archive aria-hidden="true" className="size-4" />{t('investments.closed_mark')}</span> : null}
          {kindText(t, item.kind)}
        </span>
      </div>
      <OwnerChips owners={item.owners} />
      <div className="flex flex-wrap items-center gap-3 text-sm">
        {item.figures.value
          ? <span className="tabular-nums"><Money value={item.figures.value} tabular />{item.figures.asOf ? <> · <LocalDate value={item.figures.asOf} /></> : null}</span>
          : <span className="text-muted-foreground">{t('investments.no_value')}</span>}
        <StaleBadge days={item.figures.staleDays} />
        {item.figures.value ? <Gain figures={item.figures} /> : null}
      </div>
    </li>
  );
}
