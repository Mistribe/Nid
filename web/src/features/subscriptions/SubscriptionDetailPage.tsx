import { Link, useParams } from '@tanstack/react-router';
import { useId, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { AccessBadge } from '@/components/AccessBadge';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { ProviderIcon } from '@/components/ProviderIcon';
import { DETAIL, PERSONS } from './api';
import { CoverageBand } from './CoverageBand';
import { DeletePurchase, Edits, ReadOnly } from './edits/Edits';
import { accessOf, durationText, kindText, payerText } from './format';

/** 1E2a design §4: the subscription as it stands, and its edits when the reader may write. */
export function SubscriptionDetailPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { id } = useParams({ from: '/subscriptions/$id' });
  const answer = $api.useQuery('get', DETAIL, { params: { path: { id } } });
  const people = $api.useQuery('get', PERSONS);
  const back = <Link to="/subscriptions" className="text-sm underline">{t('subscription.back')}</Link>;

  if (answer.error) return <>{back}<ProblemMessage problem={present(toProblem(answer.error))} /></>;
  if (!answer.data) return <>{back}<div aria-busy="true" aria-label={t('subscription.loading')} className="h-40 animate-pulse rounded-md border bg-muted" /></>;

  const detail = answer.data;
  const persons = people.data ?? [];
  return (
    <article className="space-y-6">
      {back}
      <header className="flex flex-wrap items-center gap-3">
        <ProviderIcon name={detail.provider?.name ?? detail.displayName} iconUrl={detail.provider?.iconUrl} color={detail.provider?.labels[0]?.color} />
        <h1 className="text-2xl font-semibold">{detail.displayName}</h1>
        <AccessBadge access={accessOf(detail, persons)} />
      </header>
      {detail.canWrite ? null : <ReadOnly detail={detail} persons={persons} />}

      <Section title={t('subscription.costs')}>
        <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1">
          <dt>{t('subscription.cash')}</dt>
          <dd>{detail.cashCost.map((m) => <Money key={m.currency} value={m} />)}</dd>
          <dt>{t('subscription.amortized')}</dt>
          <dd>{detail.amortizedCost.map((m) => <Money key={m.currency} value={m} />)}</dd>
        </dl>
        <p>
          {detail.nextChargeOn
            ? t('subscriptions.next_charge', { date: formatLocalDate(detail.nextChargeOn) })
            : detail.expiresOn ? t('subscriptions.expires', { date: formatLocalDate(detail.expiresOn) }) : t('subscription.no_next_charge')}
        </p>
        {detail.freeDays > 0 ? <p>{t('subscription.free_days', { count: detail.freeDays })}</p> : null}
      </Section>

      {detail.findings.length > 0 ? (
        <Section title={t('subscription.findings')}>
          <ul className="list-disc pl-5">{detail.findings.map((f) => <li key={`${f.code}-${f.reference ?? ''}`}>{f.message}</li>)}</ul>
        </Section>
      ) : null}

      <Section title={t('subscription.coverage')}><CoverageBand coverage={detail.coverage} /></Section>

      <Section title={t('subscription.phases')}>
        <table className="w-full text-sm">
          <caption className="sr-only">{t('subscription.phases')}</caption>
          <thead>
            <tr>
              <th scope="col" className="text-left">{t('subscription.col_start')}</th>
              <th scope="col" className="text-left">{t('subscription.col_end')}</th>
              <th scope="col" className="text-left">{t('subscription.col_price')}</th>
              <th scope="col" className="text-left">{t('subscription.col_every')}</th>
              <th scope="col" className="text-left">{t('subscription.col_payer')}</th>
            </tr>
          </thead>
          <tbody>
            {detail.phases.map((p) => (
              <tr key={p.id}>
                <td><LocalDate value={p.startsOn} /></td>
                <td>{p.endsOn ? <LocalDate value={p.endsOn} /> : t('subscription.ongoing')}</td>
                <td><Money value={p.price} tabular /></td>
                <td>{durationText(t, p.every)}</td>
                <td>{payerText(t, p.payer, persons)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </Section>

      <Section title={t('subscription.purchases')}>
        {detail.purchases.length === 0 ? <p>{t('subscription.no_purchases')}</p> : (
          <ul className="space-y-2">
            {detail.purchases.map((p) => (
              <li key={p.id} className="rounded-md border p-2">
                <p className="font-medium">{p.label}</p>
                <p className="text-sm"><LocalDate value={p.date} /> · {kindText(t, p.kind)} · <Money value={p.price} /> · {payerText(t, p.payer, persons)}</p>
                <ul className="text-sm text-muted-foreground">
                  {p.grants.map((g, i) => (
                    <li key={i}>{g.subscriptionId === detail.id
                      ? t('subscription.grant', { duration: durationText(t, g.duration) })
                      : t('subscription.grant_elsewhere', { duration: durationText(t, g.duration) })}</li>
                  ))}
                </ul>
                {detail.canWrite ? <DeletePurchase detail={detail} purchase={p} /> : null}
              </li>
            ))}
          </ul>
        )}
      </Section>

      <Section title={t('subscription.charges')}>
        <table className="w-full text-sm">
          <caption className="sr-only">{t('subscription.charges')}</caption>
          <thead>
            <tr>
              <th scope="col" className="text-left">{t('subscription.col_date')}</th>
              <th scope="col" className="text-left">{t('subscription.col_amount')}</th>
              <th scope="col" className="text-left">{t('subscription.col_state')}</th>
            </tr>
          </thead>
          <tbody>
            {detail.charges.map((c, i) => (
              <tr key={i}>
                <td><LocalDate value={c.date} /></td>
                <td><Money value={c.price} tabular /></td>
                <td>{c.isProjected ? t('subscription.projected') : t('subscription.past')}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </Section>

      {detail.canWrite ? <Edits detail={detail} /> : null}
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
