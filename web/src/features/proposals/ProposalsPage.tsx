import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { ProposalsSearch, RootSearch } from '@/app/router';
import { formatLocalDate } from '@/components/LocalDate';
import { ProblemMessage } from '@/components/ProblemMessage';
import { PROPOSALS, type ProposalView } from './api';
import { groupBySource, kindOf, originText } from './kinds';

const STATUSES = ['pending', 'approved', 'rejected'] as const;

/** Spec 4C2 §3: the proposals by status, grouped by statement. */
export function ProposalsPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const search: RootSearch & ProposalsSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const status = search.status ?? 'pending';
  const answer = $api.useQuery('get', PROPOSALS, { params: { query: { status } } });

  return (
    <div className="space-y-4">
      <h1 className="text-2xl font-semibold">{t('proposals.title')}</h1>
      <div role="tablist" aria-label={t('proposals.tabs')} className="flex gap-2">
        {STATUSES.map((s) => (
          <button key={s} role="tab" type="button" aria-selected={s === status}
            className={s === status ? 'border-b-2 border-primary px-3 py-2 font-semibold' : 'px-3 py-2'}
            onClick={() => void navigate({ to: '/proposals', search: (prev) => ({ ...prev, status: s === 'pending' ? undefined : s }), replace: true })}>
            {t(`proposals.tab.${s}`)}
          </button>
        ))}
      </div>
      <div role="tabpanel">
        {answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} /> : null}
        {answer.data && answer.data.items.length === 0 ? <p>{t(`proposals.empty.${status}`)}</p> : null}
        {answer.data ? groupBySource(answer.data.items).map((group) => <Group key={group.label ?? ''} label={group.label} items={group.items} />) : null}
      </div>
    </div>
  );
}

function Group({ label, items }: { label: string | null; items: ProposalView[] }) {
  const { t } = useTranslation();
  const name = label ?? t('proposals.no_source');
  return (
    <section aria-label={name} className="space-y-2">
      <h2 className="text-lg font-semibold">{name}</h2>
      <ul className="divide-y rounded-md border">
        {items.map((p) => (
          <li key={p.id} className="px-4 py-3">
            <Link to="/proposals/$id" params={{ id: p.id }} className="underline">
              {kindOf(p.kind)?.summary(t, p) ?? `${p.kind} — ${p.targetName ?? t('proposals.unknown_target')}`}
            </Link>
            <p className="text-sm text-muted-foreground">
              {p.status === 'pending'
                ? t('proposals.proposed_on', { origin: originText(t, p.createdVia), date: formatLocalDate(p.createdAt.slice(0, 10)) })
                : t(`proposals.decided.${p.status === 'rejected' ? 'rejected' : 'approved'}`, { date: formatLocalDate((p.decidedAt ?? p.createdAt).slice(0, 10)) })}
            </p>
          </li>
        ))}
      </ul>
    </section>
  );
}
