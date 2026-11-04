import { useParams } from '@tanstack/react-router';
import { AlertTriangle } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { components } from '@/api/schema';
import { AccessBadge } from '@/components/AccessBadge';
import { FormDialog } from '@/components/FormDialog';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { shownOr } from '@/components/page/RowMenu';
import { PageTrail } from '@/components/page/trail';
import { SharingBadges } from '@/components/ownership/SharingBadges';
import { SharingMenu } from '@/components/ownership/SharingMenu';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { ProviderIcon } from '@/components/ProviderIcon';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Section } from '@/components/page/Section';
import { DETAIL, PERSONS } from './api';
import { ContractSection } from './contract/ContractSection';
import { ContractTermsForm } from './contract/ContractTermsForm';
import { InsuranceForm } from './contract/InsuranceForm';
import { InsuranceSection } from './contract/InsuranceSection';
import { CoverageBand } from './CoverageBand';
import { DeletePurchase, ReadOnly } from './edits/Edits';
import { PriceChangeForm } from './edits/PriceChangeForm';
import { PurchaseForm } from './edits/PurchaseForm';
import { SettingsForm } from './edits/SettingsForm';
import { findingText, visibleFindings } from './findings';
import { accessOf, durationText, kindText, payerText } from './format';
import '@/locales/forms';
import { ItemDocuments } from '@/features/documents/ItemDocuments';
import '@/locales/documents';
import '@/locales/subscriptions';

type IncludedProvider = components['schemas']['IncludedProviderView'];

const TABS = ['overview', 'history', 'documents', 'settings'] as const;
/** The payments shown before « Tout afficher ». */
const CHARGES_SHOWN = 12;

/**
 * The payments around today (spec §4.6): the server lists them oldest first, the projected ones last. Up to the 3 next
 * charges and the most recent past ones before them, in that order; the whole list when it is short.
 */
function chargesWindow<T extends { isProjected: boolean }>(charges: readonly T[]): readonly T[] {
  if (charges.length <= CHARGES_SHOWN) return charges;
  const firstProjected = charges.findIndex((c) => c.isProjected);
  const end = firstProjected < 0 ? charges.length : Math.min(charges.length, firstProjected + 3);
  const start = Math.max(0, end - CHARGES_SHOWN);
  return charges.slice(start, start + CHARGES_SHOWN);
}

/**
 * 1E2a design §4, redesign §5.3, #327 §4.1: under the title the costs and the next charge; the read-only notice and the
 * findings, which concern every tab; then tabs. « Vue d'ensemble » holds the included services, the coverage, the contract
 * and the insurance; « Historique » the prices, the purchases and the payments (12 around today, then « Tout afficher »);
 * « Documents » the item's documents; « Réglages » the settings, for a writer only. Changing the price and adding a purchase
 * are the header's buttons, the contract terms and the insurance sit in its `⋯`, each in a dialog. The page owns the toast
 * and where the focus lands: a closed dialog hands it to what opened it in the header, a removed insurance to `⋯`, a deleted
 * purchase to the « Achats » heading in « Historique », the tab it was deleted from.
 */
export function SubscriptionDetailPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { id } = useParams({ from: '/subscriptions/$id' });
  const answer = $api.useQuery('get', DETAIL, { params: { path: { id } } });
  const people = $api.useQuery('get', PERSONS);
  const [tab, setTab] = useDetailTab(TABS);
  const [dialog, setDialog] = useState<'price' | 'purchase' | 'terms' | 'insurance' | null>(null);
  // Which subscription shows all its payments: another one opened on the same page starts folded.
  const [expandedFor, setExpandedFor] = useState<string | null>(null);
  const priceRef = useRef<HTMLButtonElement>(null);
  const purchaseRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLButtonElement>(null);
  const purchasesHeadingRef = useRef<HTMLHeadingElement>(null);
  // Once a dialog is gone (its focus trap with it), the focus goes back to what opened it: its button, or `⋯` for the
  // menu's entries. Never a saved row, nor a tab's content: the header is visible from any tab.
  const opened = useRef<typeof dialog>(null);
  useEffect(() => {
    if (dialog) { opened.current = dialog; return; }
    const was = opened.current;
    opened.current = null;
    if (was) shownOr((was === 'price' ? priceRef : was === 'purchase' ? purchaseRef : menuRef).current, menuRef.current)?.focus();
  }, [dialog]);

  // No `PageHeader` yet: the bar's trail still leads back to the list (no inline link of its own).
  const crumbs = [{ label: t('nav.subscriptions'), to: '/subscriptions' as const }];
  const trail = <PageTrail crumbs={crumbs} />;
  if (answer.error) return <article className="space-y-6">{trail}<ProblemMessage problem={present(toProblem(answer.error))} /></article>;
  if (!answer.data) return <article className="space-y-6">{trail}<div aria-busy="true" aria-label={t('subscription.loading')} className="h-40 shimmer rounded-md border bg-muted" /></article>;

  const detail = answer.data;
  const findings = visibleFindings(detail.findings);
  const persons = people.data ?? [];
  // The dialog hands itself back before the page announces; the effect above moves the focus.
  const close = (message?: string) => {
    setDialog(null);
    if (message) toast.success(message);
  };
  const actions = detail.canWrite ? (
    <>
      <Button ref={priceRef} type="button" onClick={() => setDialog('price')}>{t('edit.price_title')}</Button>
      <Button ref={purchaseRef} type="button" variant="outline" className="max-sm:hidden" onClick={() => setDialog('purchase')}>{t('edit.purchase_title')}</Button>
      <SharingMenu item={id} onAnnounce={toast.success} shared={detail.visibility === 'shared'} label={t('page.more_actions')} triggerRef={menuRef} items={[
        { label: t('edit.purchase_title'), phone: true, onSelect: () => setDialog('purchase') },
        { label: t('contract_terms.edit'), onSelect: () => setDialog('terms') },
        { label: detail.insurance ? t('insurance.change') : t('insurance.add'), onSelect: () => setDialog('insurance') },
      ]} />
    </>
  ) : null;
  const shown = tab === 'settings' && !detail.canWrite ? 'overview' : tab;
  const allCharges = expandedFor === id;
  const charges = allCharges ? detail.charges : chargesWindow(detail.charges);
  const nextCharge = detail.nextChargeOn
    ? { label: t('subscription.next_charge'), value: formatLocalDate(detail.nextChargeOn) }
    : detail.expiresOn
      ? { label: t('subscription.expires'), value: formatLocalDate(detail.expiresOn) }
      : { label: t('subscription.next_charge'), value: t('subscription.no_next_charge') };

  return (
    <article className="space-y-6">
      <PageHeader crumbs={crumbs} crumbLabel={detail.displayName} actions={actions}
        title={(
          <span className="inline-flex items-center gap-3">
            <ProviderIcon name={detail.provider?.name ?? detail.displayName} iconUrl={detail.provider?.iconUrl} color={detail.provider?.labels[0]?.color} />
            {detail.displayName}
          </span>
        )}
        meta={<><AccessBadge access={accessOf(detail, persons)} /><SharingBadges item={id} /></>}>
        <section aria-label={t('subscription.costs')} className="space-y-2">
          <StatLine items={[
            { label: t('subscription.cash'), value: detail.cashCost.map((m) => <Money key={m.currency} value={m} tabular />) },
            ...(detail.billedMonthly ? [] : [{ label: t('subscription.amortized'), value: detail.amortizedCost.map((m) => <Money key={m.currency} value={m} tabular />) }]),
            nextCharge,
            ...(detail.cancellation.status === 'deadline' && detail.cancellation.lastDayToCancel
              ? [{ label: t('contract_terms.cancel_deadline'), value: formatLocalDate(detail.cancellation.lastDayToCancel) }] : []),
          ]} />
          {detail.freeDays > 0 ? <p className="text-sm text-muted-foreground">{t('subscription.free_days', { count: detail.freeDays })}</p> : null}
        </section>
      </PageHeader>
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
      {dialog === 'terms' ? (
        <FormDialog title={t('contract_terms.edit')} onClose={() => close()} onDone={close}>
          {({ titleId, done }) => <ContractTermsForm detail={detail} titleId={titleId} onDone={done} />}
        </FormDialog>
      ) : null}
      {dialog === 'insurance' ? (
        <FormDialog title={t('insurance.edit')} onClose={() => close()} onDone={close}>
          {({ titleId, done }) => <InsuranceForm detail={detail} titleId={titleId} onDone={done} />}
        </FormDialog>
      ) : null}
      {dialog === 'purchase' ? (
        <FormDialog title={t('edit.purchase_title')} onClose={() => close()} onDone={close}>
          {({ titleId, done }) => <PurchaseForm detail={detail} titleId={titleId} onDone={done} />}
        </FormDialog>
      ) : null}

      <DetailTabs value={shown} onChange={setTab} tabs={[
        {
          id: 'overview', label: t('subscription.tab.overview'),
          content: (
            <>
              {detail.provider && detail.provider.included.length > 0 ? <IncludedProviders included={detail.provider.included} /> : null}
              <Section title={t('subscription.coverage')}><CoverageBand coverage={detail.coverage} /></Section>
              <ContractSection detail={detail} />
              <InsuranceSection detail={detail} onRemoved={(message) => { toast.success(message); menuRef.current?.focus(); }} />
            </>
          ),
        },
        {
          id: 'history', label: t('subscription.tab.history'),
          content: (
            <>
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
                          {detail.canWrite ? <TableCell><DeletePurchase detail={detail} purchase={p} onDeleted={(message) => { toast.success(message); purchasesHeadingRef.current?.focus(); }} /></TableCell> : null}
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
                    {charges.map((c, i) => (
                      <TableRow key={i}>
                        <TableCell><LocalDate value={c.date} /></TableCell>
                        <TableCell className="text-right"><Money value={c.price} tabular /></TableCell>
                        <TableCell>{c.isProjected ? t('subscription.projected') : t('subscription.past')}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
                {detail.charges.length > CHARGES_SHOWN ? (
                  <Button type="button" variant="link" size="sm" className="px-0" onClick={() => setExpandedFor(allCharges ? null : id)}>
                    {allCharges ? t('subscription.show_less') : t('subscription.show_all', { count: detail.charges.length })}
                  </Button>
                ) : null}
              </Section>
            </>
          ),
        },
        { id: 'documents', label: t('documents_item.title'),
          content: <ItemDocuments itemId={detail.id} canWrite={detail.canWrite} defaultRole={detail.insurance ? 'insurance' : 'invoice'} /> },
        ...(detail.canWrite ? [{
          id: 'settings' as const, label: t('subscription.tab.settings'),
          content: <Section title={t('edit.settings_title')} srTitle><SettingsForm detail={detail} /></Section>,
        }] : []),
      ]} />
    </article>
  );
}

/** What a bundle covers: the server gives the list, the link goes from the bundle to its services only. */
function IncludedProviders({ included }: { included: readonly IncludedProvider[] }) {
  const { t } = useTranslation();
  return (
    <section aria-label={t('subscription.included')} className="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm">
      <span className="text-muted-foreground">{t('subscription.included')}</span>
      <ul className="flex flex-wrap items-center gap-x-4 gap-y-1">
        {included.map((p) => (
          <li key={p.key} className="inline-flex items-center gap-1.5">
            <ProviderIcon name={p.name} iconUrl={p.iconUrl} color={p.labels[0]?.color} className="size-5 text-[0.6rem]" />
            {p.name}
          </li>
        ))}
      </ul>
    </section>
  );
}
