import { Link, useParams } from '@tanstack/react-router';
import { AlertTriangle, Archive } from 'lucide-react';
import { useId, type ReactNode, type RefObject } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { AccessBadge } from '@/components/AccessBadge';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { SOURCE } from './api';
import { kindText } from './format';
import { groupPhases } from './phases/phase';
import { PhasesTable } from './phases/PhasesTable';

/** 2C2a design §7: a source's figures, gap warnings and phases by label. Writes come with Task 6 and Task 7. */
export function IncomeSourceDetailPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { id } = useParams({ from: '/income/$id' });
  const answer = $api.useQuery('get', SOURCE, { params: { path: { id } } });
  const back = <Link to="/income" className="text-sm underline">{t('income_detail.back')}</Link>;

  if (answer.error) return <>{back}<ProblemMessage problem={present(toProblem(answer.error))} /></>;
  if (!answer.data) return <>{back}<div aria-busy="true" aria-label={t('income_detail.loading')} className="h-40 animate-pulse rounded-md border bg-muted" /></>;

  const source = answer.data;
  const running = source.phases.filter((p) => p.status === 'running');
  const groups = groupPhases(source.phases);

  return (
    <article className="space-y-6">
      {back}
      <header className="flex flex-wrap items-center gap-3">
        <h1 className="text-2xl font-semibold">{source.displayName}</h1>
        <span className="text-sm text-muted-foreground">{kindText(t, source.kind)}</span>
        {source.payerLabel ? <span className="text-sm text-muted-foreground">{source.payerLabel}</span> : null}
        <AccessBadge access={source.visibility === 'private' ? 'private' : 'shared'} />
      </header>
      {source.canWrite ? null : <p>{t('income_detail.read_only')}</p>}
      {source.ended ? (
        <p role="note" className="flex flex-wrap items-center gap-2 rounded-md border p-3">
          <Archive aria-hidden="true" className="size-4" />
          <span className="font-medium">{t('income_detail.ended_banner')}</span>
          {source.endedOn ? <span>{t('income_detail.ended_on', { date: formatLocalDate(source.endedOn) })}</span> : null}
        </p>
      ) : null}

      <Section title={t('income_detail.figures')}>
        <p>
          <span className="tabular-nums">{t('income.per_month', { amount: formatMoney(source.monthlyEquivalent) })}</span>
          {' '}<span className="text-muted-foreground">{t('income_detail.smoothed')}</span>
        </p>
        {running.length === 0 ? <p className="text-muted-foreground">{t('income_detail.no_running')}</p> : (
          <ul aria-label={t('income_detail.running_list')} className="space-y-1">
            {running.map((p) => <li key={p.id}>{t('income_detail.running_part', { label: p.label, amount: formatMoney(p.monthlyEquivalent) })}</li>)}
          </ul>
        )}
      </Section>

      {source.gapSoon.map((gap) => (
        <p key={gap.label} role="note" className="flex flex-wrap items-center gap-2">
          <AlertTriangle aria-hidden="true" className="size-4 text-status-warning" />
          {t('income_detail.gap', { label: gap.label, date: formatLocalDate(gap.endsOn) })}
        </p>
      ))}

      <Section title={t('income_detail.phases')}>
        {groups.length === 0
          ? <p className="text-muted-foreground">{t('income_detail.no_phases')}</p>
          : groups.map((g) => <PhasesTable key={g.label} label={g.label} phases={g.phases} />)}
      </Section>
    </article>
  );
}

function Section({ title, headingRef, announcement, children }: {
  title: string; headingRef?: RefObject<HTMLHeadingElement | null>; announcement?: string; children: ReactNode;
}) {
  const id = useId();
  return (
    <section aria-labelledby={id} className="space-y-2">
      <h2 id={id} ref={headingRef} tabIndex={headingRef ? -1 : undefined} className="text-lg font-semibold">{title}</h2>
      {announcement !== undefined ? <p role="status" aria-live="polite">{announcement}</p> : null}
      {children}
    </section>
  );
}
