import { Link, useNavigate, useParams, useSearch } from '@tanstack/react-router';
import { AlertTriangle, Printer } from 'lucide-react';
import { useEffect, useId } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { SelectField } from '@/components/form/SelectField';
import { formatLocalDate, monthText } from '@/components/LocalDate';
import { FilterField, FilterRow } from '@/components/page/FilterRow';
import { PageHeader } from '@/components/page/PageHeader';
import { PageTrail } from '@/components/page/trail';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { money } from '../../loans/format';
import { todayIso } from '@/lib/today';
import { usePropertyTrail } from '../propertyTrail';
import { PROPERTY_RECEIPTS } from '@/api/properties';
import { elision } from '@/lib/elision';
import '@/locales/properties';
import { PageSkeleton } from '@/components/page/skeletons';

/** The tenancy fields are completed on the lease, the address on the property's settings. */
const LEASE_FIELDS = ['tenant_names', 'landlord_name', 'landlord_address'];

/**
 * Rent receipts spec §4: the receipt of a month, as plain typography to print. The toolbar and the month selector stay out
 * of the print; every figure is the server's string; what the server could not fill is « [à compléter] », never invented.
 */
export function ReceiptPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { id } = useParams({ from: '/properties/$id/receipt' });
  const { month } = useSearch({ from: '/properties/$id/receipt' });
  const navigate = useNavigate();
  const titleId = useId();
  const dueId = useId();
  const answer = $api.useQuery('get', PROPERTY_RECEIPTS, { params: { path: { id }, query: month ? { month } : {} } });
  const view = answer.data;
  // A link kept from before (a month the ledger no longer offers) falls back to the server's default month.
  const stale = month !== undefined && answer.error ? toProblem(answer.error).code === 'realestate.receipt_month' : false;
  useEffect(() => {
    if (stale) void navigate({ to: '/properties/$id/receipt', params: { id }, search: {}, replace: true });
  }, [stale, navigate, id]);
  // No `PageHeader` yet: the bar's trail leads back to the property (no inline link of its own).
  const crumbs = usePropertyTrail(id);
  const trail = <PageTrail crumbs={crumbs} />;

  if (answer.error && !stale) return <div className="space-y-4">{trail}<ProblemMessage problem={present(toProblem(answer.error))} /></div>;
  if (!view) return <div className="space-y-4">{trail}<PageSkeleton label={t('property_receipt.loading')} stats={0} /></div>;

  const missingText = (field: string) => {
    switch (field) {
      case 'postal_address': return t('property_receipt.missing_postal_address');
      case 'tenant_names': return t('property_receipt.missing_tenant_names');
      case 'landlord_name': return t('property_receipt.missing_landlord_name');
      default: return t('property_receipt.missing_landlord_address');
    }
  };
  const todo = t('property_receipt.todo');
  const title = view.kind === 'receipt' ? t('property_receipt.title_receipt') : t('property_receipt.title_quittance');
  const period = monthText(view.month);
  const lease = view.missing.some((m) => LEASE_FIELDS.includes(m));
  const settings = view.missing.includes('postal_address');
  const landlord = view.landlordName || todo;
  const tenant = view.tenantNames || todo;

  return (
    <div className="space-y-6">
      <div className="space-y-4 print:hidden">
        <PageHeader crumbs={[{ label: t('nav.properties'), to: '/properties' }, { label: view.propertyName, to: '/properties/$id', params: { id } }]} title={t('property_receipt.link')}
          actions={view.kind === 'unpaid' ? null : <Button type="button" variant="outline" onClick={() => window.print()}><Printer aria-hidden="true" />{t('property_receipt.print')}</Button>} />
        <FilterRow label={t('common.filters')}>
          <FilterField label={t('property_receipt.month')} className="w-full sm:w-auto">
            {(labelId, controlId) => (
              <div className="min-w-0 flex-1 sm:w-60 sm:flex-none">
                <SelectField id={controlId} aria-labelledby={labelId} value={view.month} options={view.months.map((m) => ({ value: m, label: monthText(m) }))}
                  onValueChange={(next) => void navigate({ to: '/properties/$id/receipt', params: { id }, search: { month: next } })} />
              </div>
            )}
          </FilterField>
        </FilterRow>
        {view.missing.length > 0 ? (
          <div role="alert" className="flex gap-2 rounded-md border border-status-warning/40 bg-status-warning/10 p-3 text-sm text-status-warning-text">
            <AlertTriangle aria-hidden="true" className="mt-0.5 size-4 shrink-0" />
            <div className="space-y-1">
              <p>{t('property_receipt.missing')} {view.missing.map(missingText).join(', ')}.</p>
              <p className="flex gap-4">
                {lease ? <Link to="/properties/$id" params={{ id }} search={{ tab: 'rental' }} className="underline">{t('property_receipt.fix_lease')}</Link> : null}
                {settings ? <Link to="/properties/$id" params={{ id }} search={{ tab: 'settings' }} className="underline">{t('property_receipt.fix_settings')}</Link> : null}
              </p>
            </div>
          </div>
        ) : null}
      </div>

      {view.kind === 'unpaid' ? <p className="text-muted-foreground">{t('property_receipt.unpaid')}</p> : (
        <article aria-labelledby={titleId} className="mx-auto max-w-2xl space-y-6 bg-white p-6 text-black print:max-w-none print:p-0">
          <h2 id={titleId} className="text-2xl font-semibold">{title}</h2>
          <p className="font-medium">{t('property_receipt.period', { month: period, context: elision(period) })}</p>
          <dl className="space-y-3">
            <div><dt className="text-sm font-semibold">{t('property_receipt.dwelling')}</dt><dd className="whitespace-pre-line">{view.postalAddress || todo}</dd></div>
            <div><dt className="text-sm font-semibold">{t('property_receipt.landlord')}</dt><dd className="whitespace-pre-line">{landlord}{'\n'}{view.landlordAddress || todo}</dd></div>
            <div><dt className="text-sm font-semibold">{t('property_receipt.tenant')}</dt><dd>{tenant}</dd></div>
          </dl>
          {view.kind === 'receipt' ? <h3 id={dueId} className="text-sm font-semibold">{t('property_receipt.due')}</h3> : null}
          <dl aria-labelledby={view.kind === 'receipt' ? dueId : undefined} className="max-w-xs space-y-1 tabular-nums">
            <div className="flex justify-between"><dt>{t('property_receipt.rent')}</dt><dd>{money(view.currency, view.rent)}</dd></div>
            <div className="flex justify-between"><dt>{t('property_receipt.charges')}</dt><dd>{money(view.currency, view.charges)}</dd></div>
            <div className="flex justify-between border-t border-black pt-1 font-semibold"><dt>{t('property_receipt.total')}</dt><dd>{money(view.currency, view.total)}</dd></div>
          </dl>
          {view.kind === 'receipt' ? (
            <dl className="max-w-xs tabular-nums">
              <div className="flex justify-between font-semibold"><dt>{t('property_receipt.paid')}</dt><dd>{money(view.currency, view.paid)}</dd></div>
            </dl>
          ) : null}
          {view.paidOn ? <p>{t('property_receipt.paid_on', { date: formatLocalDate(view.paidOn) })}</p> : null}
          <p>
            {view.kind === 'receipt'
              ? t('property_receipt.statement_receipt', { landlord, tenant, paid: money(view.currency, view.paid), month: period, context: elision(period) })
              : t('property_receipt.statement_quittance', { landlord, tenant, total: money(view.currency, view.total), month: period, context: elision(period) })}
          </p>
          {view.kind === 'receipt' ? <p className="font-semibold">{t('property_receipt.partial')}</p> : null}
          <p>{t('property_receipt.made_on', { date: formatLocalDate(todayIso()) })}</p>
        </article>
      )}
    </div>
  );
}
