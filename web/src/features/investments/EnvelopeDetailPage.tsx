import { Link, useParams } from '@tanstack/react-router';
import { AlertTriangle } from 'lucide-react';
import { useId, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { AccessBadge } from '@/components/AccessBadge';
import { LocalDate } from '@/components/LocalDate';
import { formatMoney, Money } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { ENVELOPE } from './api';
import { Gain, StaleBadge } from './Figures';
import { directionOf, flowKindText, kindText } from './format';

/** 2B2a design §6: the envelope's figures, warnings, and its values and flows, newest first, read-only for now
 * (Task 5 adds the quick-value/add-flow forms and the rows' « Corriger »/« Supprimer » actions). */
export function EnvelopeDetailPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { id } = useParams({ from: '/investments/$id' });
  const answer = $api.useQuery('get', ENVELOPE, { params: { path: { id } } });
  const back = <Link to="/investments" className="text-sm underline">{t('envelope.back')}</Link>;

  if (answer.error) return <>{back}<ProblemMessage problem={present(toProblem(answer.error))} /></>;
  if (!answer.data) return <>{back}<div aria-busy="true" aria-label={t('envelope.loading')} className="h-40 animate-pulse rounded-md border bg-muted" /></>;

  const detail = answer.data;
  const figures = detail.figures;
  const values = [...detail.valuations].sort((a, b) => (a.asOf < b.asOf ? 1 : a.asOf > b.asOf ? -1 : 0));
  const flows = [...detail.flows].sort((a, b) => (a.occurredOn < b.occurredOn ? 1 : a.occurredOn > b.occurredOn ? -1 : 0));
  const sinceShown = directionOf(figures.contributionsSince.amount) !== 'flat' || directionOf(figures.withdrawalsSince.amount) !== 'flat';
  const feesShown = !!figures.fees && directionOf(figures.fees.amount) !== 'flat';
  const gainAfterFeesShown = !!figures.gainAfterFees && directionOf(figures.gainAfterFees.amount) !== 'flat';
  const incomeShown = !!figures.income && directionOf(figures.income.amount) !== 'flat';

  return (
    <article className="space-y-6">
      {back}
      <header className="flex flex-wrap items-center gap-3">
        <h1 className="text-2xl font-semibold">{detail.displayName}</h1>
        <span className="text-sm text-muted-foreground">{kindText(t, detail.kind)}</span>
        <AccessBadge access={detail.visibility === 'private' ? 'private' : 'shared'} />
      </header>
      {detail.canWrite ? null : <p>{t('envelope.read_only')}</p>}

      <Section title={t('envelope.figures')}>
        <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1">
          <dt>{t('envelope.value')}</dt>
          <dd>
            {figures.value ? (
              <>
                <Money value={figures.value} tabular />
                {figures.asOf ? <> · <LocalDate value={figures.asOf} /></> : null}
                {' '}<StaleBadge days={figures.staleDays} />
              </>
            ) : <span className="text-muted-foreground">{t('investments.no_value')}</span>}
          </dd>
          <dt>{t('envelope.net_contributions')}</dt>
          <dd>
            {figures.netContributions
              ? <Money value={figures.netContributions} tabular />
              : <span className="text-muted-foreground">{t('investments.no_value')}</span>}
          </dd>
          <dt>{t('envelope.gain')}</dt>
          <dd><Gain figures={figures} /> <span className="text-muted-foreground">{t('envelope.before_tax')}</span></dd>
          {feesShown && figures.fees ? (
            <>
              <dt>{t('envelope.fees')}</dt>
              <dd><Money value={figures.fees} tabular /></dd>
            </>
          ) : null}
          {gainAfterFeesShown && figures.gainAfterFees ? (
            <>
              <dt>{t('envelope.gain_after_fees')}</dt>
              <dd><Money value={figures.gainAfterFees} tabular /></dd>
            </>
          ) : null}
          {incomeShown && figures.income ? (
            <>
              <dt>{t('envelope.income')}</dt>
              <dd><Money value={figures.income} tabular /></dd>
            </>
          ) : null}
        </dl>
        {sinceShown ? (
          <p>{t('envelope.since', {
            contributions: formatMoney(figures.contributionsSince),
            withdrawals: formatMoney(figures.withdrawalsSince),
          })}</p>
        ) : null}
        {figures.missingOpening ? (
          <p role="note" className="flex items-center gap-2">
            <AlertTriangle aria-hidden="true" className="size-4 text-status-warning" />
            {t('envelope.missing_opening')}
          </p>
        ) : null}
      </Section>

      {/* Task 5 fills this with the quick-value/add-flow forms, when detail.canWrite. */}

      <Section title={t('envelope.values')}>
        <table className="w-full text-sm" aria-label={t('envelope.values_table')}>
          <thead>
            <tr>
              <th scope="col" className="text-left">{t('envelope.date')}</th>
              <th scope="col" className="text-left">{t('envelope.amount')}</th>
              <th scope="col" className="text-left">{t('envelope.note')}</th>
              <th scope="col" className="text-left">{t('envelope.actions')}</th>
            </tr>
          </thead>
          <tbody>
            {values.map((v) => (
              <tr key={v.id}>
                <td><LocalDate value={v.asOf} /></td>
                <td><Money value={v.amount} tabular /></td>
                <td>{v.note}</td>
                <td />
              </tr>
            ))}
          </tbody>
        </table>
      </Section>

      <Section title={t('envelope.flows')}>
        <table className="w-full text-sm" aria-label={t('envelope.flows_table')}>
          <thead>
            <tr>
              <th scope="col" className="text-left">{t('envelope.date')}</th>
              <th scope="col" className="text-left">{t('envelope.kind')}</th>
              <th scope="col" className="text-left">{t('envelope.amount')}</th>
              <th scope="col" className="text-left">{t('envelope.note')}</th>
              <th scope="col" className="text-left">{t('envelope.actions')}</th>
            </tr>
          </thead>
          <tbody>
            {flows.map((f) => (
              <tr key={f.id}>
                <td><LocalDate value={f.occurredOn} /></td>
                <td>{flowKindText(t, f)}</td>
                <td><Money value={f.amount} tabular /></td>
                <td>{f.note}</td>
                <td />
              </tr>
            ))}
          </tbody>
        </table>
      </Section>
    </article>
  );
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  const id = useId();
  return (
    <section aria-labelledby={id} className="space-y-2">
      <h2 id={id} className="text-lg font-semibold">{title}</h2>
      {children}
    </section>
  );
}
