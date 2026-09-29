import { Link, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { RootSearch } from '@/app/router';
import { EmptyState } from '@/components/EmptyState';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { ENVELOPES, type EnvelopeSummary } from './api';
import { kindText } from './format';
import { Gain, StaleBadge } from './Figures';

/** The list of envelopes with their value, gain and staleness (2B2a design). */
export function EnvelopesPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const search: RootSearch = useSearch({ strict: false });
  const scope = search.scope === 'me' ? 'me' : 'household';
  const answer = $api.useQuery('get', ENVELOPES, { params: { query: { scope } } });

  const header = (
    <div className="flex flex-wrap items-center justify-between gap-3">
      <h1 className="text-2xl font-semibold">{t('investments.title')}</h1>
      <Link to="/investments/new" className="rounded-md bg-primary px-3 py-2 text-sm text-primary-foreground">{t('investments.add')}</Link>
    </div>
  );

  if (answer.error) return <>{header}<ProblemMessage problem={present(toProblem(answer.error))} /></>;

  if (!answer.data)
    return (
      <>
        {header}
        <ul aria-busy="true" aria-label={t('investments.loading')} className="space-y-2">
          {[0, 1, 2].map((i) => <li key={i} className="h-16 animate-pulse rounded-md border bg-muted" />)}
        </ul>
      </>
    );

  const data = answer.data;
  if (data.items.length === 0)
    return (
      <>
        {header}
        <EmptyState title={t('investments.empty_title')} body={t('investments.empty_body')}
          action={<Link to="/investments/new" className="rounded-md bg-primary px-3 py-2 text-primary-foreground">{t('investments.add')}</Link>} />
      </>
    );

  return (
    <div className="space-y-4">
      {header}
      <p className="text-sm text-muted-foreground">{t('investments.before_tax')}</p>
      <ul aria-label={t('investments.list')} className="space-y-2">
        {data.items.map((item) => <Row key={item.id} item={item} />)}
      </ul>
    </div>
  );
}

function Row({ item }: { item: EnvelopeSummary }) {
  const { t } = useTranslation();
  return (
    <li className="space-y-1 rounded-md border p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <Link to="/investments/$id" params={{ id: item.id }} className="font-medium hover:underline">{item.displayName}</Link>
        <span className="text-sm text-muted-foreground">{kindText(t, item.kind)}</span>
      </div>
      <div className="flex flex-wrap items-center gap-3 text-sm">
        {item.figures.value
          ? <span className="tabular-nums"><Money value={item.figures.value} tabular />{item.figures.asOf ? <> · <LocalDate value={item.figures.asOf} /></> : null}</span>
          : <span className="text-muted-foreground">{t('investments.no_value')}</span>}
        <StaleBadge days={item.figures.staleDays} />
        <Gain figures={item.figures} />
      </div>
    </li>
  );
}
