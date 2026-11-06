import { useQueryClient } from '@tanstack/react-query';
import { useParams } from '@tanstack/react-router';
import { AlertTriangle, Pencil } from 'lucide-react';
import { lazy, Suspense, useRef, useState } from 'react';
import { FormProvider, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { AccessBadge } from '@/components/AccessBadge';
import { ErrorPage } from '@/components/ErrorPage';
import { FormDialog } from '@/components/FormDialog';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney, Money } from '@/components/Money';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { Section } from '@/components/page/Section';
import { StatLine } from '@/components/page/StatLine';
import { PageTrail } from '@/components/page/trail';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Terms } from '../../loans/detail/Terms';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { SALE_SCENARIO, SCENARIOS, type SaleResult, type SaleScenario } from '../api';
import { fromSale, SaleFields, salePath, toSaleDocument, type SaleDraft } from './SaleFields';
import '@/locales/scenarios';
import { Loading, PageSkeleton, SectionSkeleton } from '@/components/page/skeletons';

// The chart (Recharts) and the tax page (the tax strings) load with their tab.
const ProjectionTab = lazy(() => import('../ProjectionTab'));
const TaxTab = lazy(() => import('./TaxTab'));

const TABS = ['sale', 'tax', 'projection'] as const;

/**
 * Issue #416 `/scenarios/sale/:id`, the purchase's layout: the key figures inline under the name, then « Vente » (from the price to
 * the net seller, what stops each month), « Impôt » (the capital gain as « Impôt si vente » reads it) and « Projection ». Every figure
 * is the server's; an edit PUTs the whole document.
 */
export function SaleScenarioPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { id } = useParams({ from: '/scenarios/sale/$id' });
  const answer = $api.useQuery('get', SALE_SCENARIO, { params: { path: { id } } });
  const [tab, setTab] = useDetailTab(TABS);
  const [editing, setEditing] = useState(false);
  const editRef = useRef<HTMLButtonElement>(null);

  const crumbs = [{ label: t('scenarios.title'), to: '/scenarios' as const }];
  if (answer.error) return <ErrorPage problem={present(toProblem(answer.error))} crumbs={crumbs} onRetry={() => void answer.refetch()} />;
  if (!answer.data) return <><PageTrail crumbs={crumbs} /><PageSkeleton label={t('scenarios.sale.loading')} stats={4} tabs={3} /></>;

  const scenario = answer.data;
  const { sale, gap } = scenario.result;
  const dash = t('scenarios.dash');
  const tabFallback = <Loading label={t('scenarios.sale.loading')}><SectionSkeleton /></Loading>;

  return (
    <article className="space-y-6">
      <PageHeader crumbs={crumbs} title={scenario.name}
        meta={<><span className="text-sm text-muted-foreground">{t('scenarios.kind.property_sale')}</span>
          <AccessBadge access={scenario.visibility === 'private' ? 'private' : 'shared'} /></>}
        actions={scenario.canWrite ? (
          <Button ref={editRef} type="button" variant="outline" onClick={() => setEditing(true)}><Pencil aria-hidden="true" />{t('scenarios.sale.edit')}</Button>
        ) : null}>
        <section aria-label={t('scenarios.sale.figures')} className="space-y-2">
          <StatLine items={[
            { label: t('scenarios.sale.net_seller'), value: sale ? <Money value={sale.netSeller} tabular /> : dash },
            { label: t('scenarios.sale.tax'), value: sale ? <Money value={sale.tax} tabular /> : dash },
            { label: t('scenarios.sale.loans_repaid'), value: sale ? <Money value={sale.loansRepaid} tabular /> : dash },
            { label: t('scenarios.sale.monthly_change'), value: sale ? <Money value={sale.monthlyChange} tabular /> : dash },
            { label: t('scenarios.sale.gap'), value: <Money value={gap} tabular /> },
          ]} />
          <p className="text-sm text-muted-foreground">{t('scenarios.summary.reminder')}</p>
        </section>
      </PageHeader>
      {scenario.canWrite ? null : <p>{t('scenarios.detail.read_only')}</p>}

      {scenario.canWrite && editing ? (
        <FormDialog title={t('scenarios.sale.edit')} onClose={() => setEditing(false)}
          onDone={(message) => { setEditing(false); toast.success(message); editRef.current?.focus(); }}>
          {({ done }) => <EditForm scenario={scenario} done={done} />}
        </FormDialog>
      ) : null}

      <DetailTabs value={tab} onChange={setTab} tabs={[
        { id: 'sale', label: t('scenarios.sale.tab.sale'), content: <SaleTab result={scenario.result} /> },
        { id: 'tax', label: t('scenarios.sale.tab.tax'), content: <Suspense fallback={tabFallback}><TaxTab result={scenario.result} /></Suspense> },
        { id: 'projection', label: t('scenarios.sale.tab.projection'), content: <Suspense fallback={tabFallback}><ProjectionTab result={scenario.result} subject="sale" /></Suspense> },
      ]} />
    </article>
  );
}

/** « Vente »: from the price to the net seller, what stops each month, then the warnings. */
function SaleTab({ result }: { result: SaleResult }) {
  const { t } = useTranslation();
  const { sale } = result;
  return (
    <>
      {sale ? (
        <div className="grid gap-6 lg:grid-cols-2">
          <Section title={t('scenarios.sale.breakdown', { date: formatLocalDate(sale.soldOn) })}>
            <Terms items={[
              [t('scenarios.sale.price'), formatMoney(sale.price)],
              [t('scenarios.sale.agency_fees'), formatMoney(sale.agencyFees)],
              [t('scenarios.sale.tax'), formatMoney(sale.tax)],
              [t('scenarios.sale.loans_repaid'), formatMoney(sale.loansRepaid)],
              [t('scenarios.sale.penalties'), formatMoney(sale.penalties)],
              [t('scenarios.sale.net_seller'), formatMoney(sale.netSeller)],
            ]} />
          </Section>
          <Section title={t('scenarios.sale.monthly')}>
            <Terms items={[
              [t('scenarios.sale.loan_payments'), formatMoney(sale.loanPayments)],
              [t('scenarios.sale.property_net'), formatMoney(sale.propertyNet)],
              [t('scenarios.sale.monthly_total'), formatMoney(sale.monthlyChange)],
            ]} />
          </Section>
        </div>
      ) : <p className="text-sm text-muted-foreground">{t('scenarios.sale.hidden')}</p>}
      <Section title={t('scenarios.detail.warnings')}>
        {result.warnings.length === 0 ? <p className="text-sm text-muted-foreground">{t('scenarios.detail.no_warnings')}</p> : (
          <ul className="space-y-1">
            {result.warnings.map((w) => (
              <li key={w.code} className="flex items-start gap-2">
                <AlertTriangle aria-hidden="true" className="mt-0.5 size-4 shrink-0 text-status-warning" />
                {t(`scenarios.sale.warning.${w.code}`, { defaultValue: t('scenarios.warning.unknown', { code: w.code }) })}
              </li>
            ))}
          </ul>
        )}
      </Section>
    </>
  );
}

function EditForm({ scenario, done }: { scenario: SaleScenario; done: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const form = useForm<SaleDraft>({ defaultValues: fromSale(scenario) });
  const scope = scenario.scope === 'me' ? 'me' : 'household';
  const errors = useSubmitErrors(form.setError, salePath);
  const save = $api.useMutation('put', SALE_SCENARIO, {
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['get', SALE_SCENARIO] });
      void queryClient.invalidateQueries({ queryKey: ['get', SCENARIOS] });
      done(t('scenarios.sale.saved'));
    },
    onError: errors.onError,
  });
  return (
    <div className="space-y-4">
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      <FormProvider {...form}>
        <form className="space-y-4" noValidate onSubmit={(e) => void form.handleSubmit((v) => {
          errors.clear();
          save.mutate({ params: { path: { id: scenario.id } }, body: { name: v.name.trim(), visibility: v.visibility, scope, document: toSaleDocument(v) } });
        })(e)}>
          <SaleFields scope={scope} />
          <div className="flex gap-2">
            <Button type="submit" disabled={save.isPending}>{t('scenarios.sale.save')}</Button>
            <Button type="button" variant="ghost" onClick={() => done()}>{t('common.cancel')}</Button>
          </div>
        </form>
      </FormProvider>
    </div>
  );
}
