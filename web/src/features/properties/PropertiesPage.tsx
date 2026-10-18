import { Link, useSearch } from '@tanstack/react-router';
import { AlertTriangle } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { RootSearch } from '@/app/router';
import { AccessBadge } from '@/components/AccessBadge';
import { EmptyState } from '@/components/EmptyState';
import { formatLocalDate } from '@/components/LocalDate';
import { OwnerChips } from '@/components/OwnerChips';
import { ProblemMessage } from '@/components/ProblemMessage';
import { money, percent } from '../loans/format';
import { PROPERTIES, type PropertySummary } from './api';
import { typeText } from './format';

/** The properties with their value today and their net value (3D1 design §4). */
export function PropertiesPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const search: RootSearch = useSearch({ strict: false });
  const scope = search.scope === 'me' ? 'me' : 'household';
  const answer = $api.useQuery('get', PROPERTIES, { params: { query: { scope } } });
  const add = <Link to="/properties/new" className="rounded-md bg-primary px-3 py-2 text-sm text-primary-foreground">{t('properties.add')}</Link>;

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-2xl font-semibold">{t('properties.title')}</h1>
        {add}
      </div>
      {answer.error ? (
        <ProblemMessage problem={present(toProblem(answer.error))} />
      ) : !answer.data ? (
        <ul aria-busy="true" aria-label={t('properties.loading')} className="space-y-2">
          {[0, 1, 2].map((i) => <li key={i} className="h-16 animate-pulse rounded-md border bg-muted" />)}
        </ul>
      ) : answer.data.items.length === 0 ? (
        <EmptyState title={t('properties.empty_title')} body={t('properties.empty_body')} action={add} />
      ) : (
        <ul aria-label={t('properties.list')} className="space-y-2">
          {answer.data.items.map((item) => <Row key={item.id} item={item} />)}
        </ul>
      )}
    </div>
  );
}

function Row({ item }: { item: PropertySummary }) {
  const { t } = useTranslation();
  return (
    <li className="space-y-1 rounded-md border p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="flex flex-wrap items-baseline gap-2">
          <Link to="/properties/$id" params={{ id: item.id }} className="font-medium hover:underline">{item.name}</Link>
          <span className="text-sm text-muted-foreground">{typeText(t, item.type)}</span>
        </span>
        <AccessBadge access={item.visibility === 'private' ? 'private' : 'shared'} />
      </div>
      <OwnerChips owners={item.owners} />
      <div className="flex flex-wrap gap-x-4 gap-y-1 text-sm tabular-nums">
        {item.value && item.valueOn ? (
          <span>{t('properties.value', { amount: money(item.currency, item.value), date: formatLocalDate(item.valueOn) })}</span>
        ) : (
          <span>{t('properties.not_estimated')}</span>
        )}
        {item.stale ? (
          <span className="flex items-center gap-1"><AlertTriangle aria-hidden="true" className="size-4" />{t('properties.stale')}</span>
        ) : null}
        {item.netValue ? <span>{t('properties.net_value', { amount: money(item.currency, item.netValue) })}</span> : null}
        {/* Spec 3D2 §5: the server's fractions in %, « — » when it gives none. */}
        <span>{t('properties.gross_yield', { rate: item.grossYield ? percent(t, item.grossYield) : t('properties.unknown_rate') })}</span>
        <span>{t('properties.vacancy', { rate: item.vacancyRate ? percent(t, item.vacancyRate) : t('properties.unknown_rate') })}</span>
      </div>
    </li>
  );
}
