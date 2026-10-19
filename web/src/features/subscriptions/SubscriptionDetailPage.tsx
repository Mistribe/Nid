import { Link, useParams } from '@tanstack/react-router';
import { AlertTriangle } from 'lucide-react';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { AccessBadge } from '@/components/AccessBadge';
import { FormDialog } from '@/components/FormDialog';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { PageHeader } from '@/components/page/PageHeader';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { ProviderIcon } from '@/components/ProviderIcon';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Section } from '../income/Section';
import { DETAIL, PERSONS } from './api';
import { CoverageBand } from './CoverageBand';
import { DeletePurchase, ReadOnly } from './edits/Edits';
import { PriceChangeForm } from './edits/PriceChangeForm';
import { PurchaseForm } from './edits/PurchaseForm';
import { SettingsForm } from './edits/SettingsForm';
import { findingText, visibleFindings } from './findings';
import { accessOf, durationText, kindText, payerText } from './format';

/**
 * 1E2a design §4, redesign §5.3: one page without tabs. Under the title the costs and the next charge; the findings in a
 * notice; the coverage, the prices, the purchases and the payments as compact tables. Changing the price and adding a
 * purchase are the header's actions, each in a dialog; the page owns the announcement and where the focus lands.
 */
export function SubscriptionDetailPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { id } = useParams({ from: '/subscriptions/$id' });
  const answer = $api.useQuery('get', DETAIL, { params: { path: { id } } });
  const people = $api.useQuery('get', PERSONS);
  const [dialog, setDialog] = useState<'price' | 'purchase' | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const priceRef = useRef<HTMLButtonElement>(null);
  const purchaseRef = useRef<HTMLButtonElement>(null);
  const purchasesHeadingRef = useRef<HTMLHeadingElement>(null);
  const back = <Link to="/subscriptions" className="text-sm underline">{t('subscription.back')}</Link>;
  // Mounted from the first render, loading included, so the text written later is announced.
  const status = <p role="status" aria-live="polite">{announcement}</p>;

  if (answer.error) return <article className="space-y-6">{back}{status}<ProblemMessage problem={present(toProblem(answer.error))} /></article>;
  if (!answer.data) return <article className="space-y-6">{back}{status}<div aria-busy="true" aria-label={t('subscription.loading')} className="h-40 animate-pulse rounded-md border bg-muted" /></article>;

  const detail = answer.data;
  const findings = visibleFindings(detail.findings);
  const persons = people.data ?? [];
  // The dialog hands itself back before the page announces and refocuses its button (a saved row is never the target).
  const close = (message?: string) => {
    const was = dialog;
    setDialog(null);
    if (message) setAnnouncement(message);
    (was === 'price' ? priceRef : purchaseRef).current?.focus();
  };
  const actions = detail.canWrite ? (
    <>
      <Button ref={priceRef} type="button" onClick={() => { setAnnouncement(''); setDialog('price'); }}>{t('edit.price_title')}</Button>
      <Button ref={purchaseRef} type="button" variant="outline" onClick={() => { setAnnouncement(''); setDialog('purchase'); }}>{t('edit.purchase_title')}</Button>
    </>
  ) : null;
  const nextCharge = detail.nextChargeOn
    ? { label: t('subscription.next_charge'), value: formatLocalDate(detail.nextChargeOn) }
    : detail.expiresOn
      ? { label: t('subscription.expires'), value: formatLocalDate(detail.expiresOn) }
      : { label: t('subscription.next_charge'), value: t('subscription.no_next_charge') };

  return (
    <article className="space-y-6">
      <PageHeader crumbs={[{ label: t('nav.subscriptions'), to: '/subscriptions' }]} actions={actions}
        title={(
          <span className="inline-flex items-center gap-3">
            <ProviderIcon name={detail.provider?.name ?? detail.displayName} iconUrl={detail.provider?.iconUrl} color={detail.provider?.labels[0]?.color} />
            {detail.displayName}
          </span>
        )}
        meta={<AccessBadge access={accessOf(detail, persons)} />}>
        <section aria-label={t('subscription.costs')} className="space-y-2">
          <StatLine items={[
            { label: t('subscription.cash'), value: detail.cashCost.map((m) => <Money key={m.currency} value={m} tabular />) },
            { label: t('subscription.amortized'), value: detail.amortizedCost.map((m) => <Money key={m.currency} value={m} tabular />) },
            nextCharge,
          ]} />
          {detail.freeDays > 0 ? <p className="text-sm text-muted-foreground">{t('subscription.free_days', { count: detail.freeDays })}</p> : null}
        </section>
      </PageHeader>
      {status}
      {detail.canWrite ? null : <ReadOnly detail={detail} persons={persons} />}
      {findings.length > 0 ? (
        <ul aria-label={t('subscription.findings')} className="space-y-1 rounded-md border p-3">
          {findings.map((f) => (
            <li key={`${f.code}-${f.reference ?? ''}`} className="flex items-center gap-2">
              <AlertTriangle aria-hidden="true" className="size-4 shrink-0 text-status-warning" />
              {findingText(t, f)}
            </li>
          ))}
        </ul>
      ) : null}
      {dialog === 'price' ? (
        <FormDialog title={t('edit.price_title')} onClose={() => close()} onDone={close}>
          {({ titleId, done }) => <PriceChangeForm detail={detail} titleId={titleId} onDone={done} />}
        </FormDialog>
      ) : null}
      {dialog === 'purchase' ? (
        <FormDialog title={t('edit.purchase_title')} onClose={() => close()} onDone={close}>
          {({ titleId, done }) => <PurchaseForm detail={detail} titleId={titleId} onDone={done} />}
        </FormDialog>
      ) : null}

      <Section title={t('subscription.coverage')}><CoverageBand coverage={detail.coverage} /></Section>

      <Section title={t('subscription.phases')}>
        <Table aria-label={t('subscription.phases')}>
          <TableHeader>
            <TableRow>
              <TableHead>{t('subscription.col_start')}</TableHead>
              <TableHead>{t('subscription.col_end')}</TableHead>
              <TableHead className="text-right">{t('subscription.col_price')}</TableHead>
              <TableHead>{t('subscription.col_every')}</TableHead>
              <TableHead>{t('subscription.col_payer')}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {detail.phases.map((p) => (
              <TableRow key={p.id}>
                <TableCell><LocalDate value={p.startsOn} /></TableCell>
                <TableCell>{p.endsOn ? <LocalDate value={p.endsOn} /> : t('subscription.ongoing')}</TableCell>
                <TableCell className="text-right"><Money value={p.price} tabular /></TableCell>
                <TableCell>{durationText(t, p.every)}</TableCell>
                <TableCell>{payerText(t, p.payer, persons)}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Section>

      <Section title={t('subscription.purchases')} headingRef={purchasesHeadingRef}>
        {detail.purchases.length === 0 ? <p className="text-muted-foreground">{t('subscription.no_purchases')}</p> : (
          <Table aria-label={t('subscription.purchases')}>
            <TableHeader>
              <TableRow>
                <TableHead>{t('subscription.col_date')}</TableHead>
                <TableHead>{t('subscription.col_label')}</TableHead>
                <TableHead className="text-right">{t('subscription.col_price')}</TableHead>
                <TableHead>{t('subscription.col_payer')}</TableHead>
                <TableHead>{t('subscription.col_grants')}</TableHead>
                {detail.canWrite ? <TableHead className="w-0"><span className="sr-only">{t('subscription.col_actions')}</span></TableHead> : null}
              </TableRow>
            </TableHeader>
            <TableBody>
              {detail.purchases.map((p) => (
                <TableRow key={p.id}>
                  <TableCell><LocalDate value={p.date} /></TableCell>
                  <TableCell className="whitespace-normal">
                    <span className="font-medium">{p.label}</span>
                    <span className="block text-xs text-muted-foreground">{kindText(t, p.kind)}</span>
                  </TableCell>
                  <TableCell className="text-right"><Money value={p.price} tabular /></TableCell>
                  <TableCell>{payerText(t, p.payer, persons)}</TableCell>
                  <TableCell className="whitespace-normal">
                    {p.grants.map((g, i) => (
                      <span key={i} className="block">{g.subscriptionId === detail.id
                        ? t('subscription.grant', { duration: durationText(t, g.duration) })
                        : t('subscription.grant_elsewhere', { duration: durationText(t, g.duration) })}</span>
                    ))}
                  </TableCell>
                  {detail.canWrite ? <TableCell><DeletePurchase detail={detail} purchase={p} onDeleted={(message) => { setAnnouncement(message); purchasesHeadingRef.current?.focus(); }} /></TableCell> : null}
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Section>

      <Section title={t('subscription.charges')}>
        <Table aria-label={t('subscription.charges')}>
          <TableHeader>
            <TableRow>
              <TableHead>{t('subscription.col_date')}</TableHead>
              <TableHead className="text-right">{t('subscription.col_amount')}</TableHead>
              <TableHead>{t('subscription.col_state')}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {detail.charges.map((c, i) => (
              <TableRow key={i}>
                <TableCell><LocalDate value={c.date} /></TableCell>
                <TableCell className="text-right"><Money value={c.price} tabular /></TableCell>
                <TableCell>{c.isProjected ? t('subscription.projected') : t('subscription.past')}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Section>

      {detail.canWrite ? <Section title={t('edit.settings_title')}><SettingsForm detail={detail} /></Section> : null}
    </article>
  );
}
