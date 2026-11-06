import { Link, useNavigate, useParams, useSearch } from '@tanstack/react-router';
import { AlertTriangle, Printer } from 'lucide-react';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { Segmented } from '@/components/form/Segmented';
import { formatLocalDate, monthText } from '@/components/LocalDate';
import { FilterField, FilterRow } from '@/components/page/FilterRow';
import { PageHeader } from '@/components/page/PageHeader';
import { PageTrail } from '@/components/page/trail';
import { ErrorPage } from '@/components/ErrorPage';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { money } from '../../loans/format';
import { todayIso } from '../../subscriptions/today';
import { usePropertyTrail } from '../propertyTrail';
import { PROPERTY_ARREARS } from '../api';
import '@/locales/properties';

/** The delays offered; any other one in the URL (1 to 60) still sets the deadline. */
const DELAYS = [8, 15, 30];
const DEFAULT_DELAY = 15;
/** The tenancy fields are completed on the lease, the address on the property's settings. */
const LEASE_FIELDS = ['tenant_names', 'landlord_name', 'landlord_address'];

/** Today plus `days`, as an ISO calendar date (a date, never an amount). */
function deadlineIso(days: number) {
  const due = new Date();
  due.setDate(due.getDate() + days);
  return todayIso(due);
}

/**
 * The reminder letter of a property's overdue rent, as plain typography to print (same style as the receipt). The toolbar, the
 * delay and the warnings stay out of the print; every figure is the server's string; what the server could not fill is
 * « [à compléter] ». No letter from the server means nothing is overdue: no document, no print.
 */
export function ArrearsLetterPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { id } = useParams({ from: '/properties/$id/arrears-letter' });
  const { days = DEFAULT_DELAY } = useSearch({ from: '/properties/$id/arrears-letter' });
  const navigate = useNavigate();
  const titleId = useId();
  const answer = $api.useQuery('get', PROPERTY_ARREARS, { params: { path: { id } } });
  const view = answer.data;
  // No `PageHeader` yet: the bar's trail leads back to the property (no inline link of its own).
  const crumbs = usePropertyTrail(id);
  const trail = <PageTrail crumbs={crumbs} />;

  if (answer.error) return <ErrorPage problem={present(toProblem(answer.error))} crumbs={crumbs} onRetry={() => void answer.refetch()} />;
  if (!view) return <div className="space-y-4">{trail}<div aria-busy="true" aria-label={t('property_arrears.loading')} className="h-40 animate-pulse rounded-md border bg-muted" /></div>;

  const letter = view.letter;
  const header = (
    <PageHeader crumbs={[{ label: t('nav.properties'), to: '/properties' }, { label: view.propertyName, to: '/properties/$id', params: { id } }]} title={t('property_arrears.letter')}
      actions={letter ? <Button type="button" variant="outline" onClick={() => window.print()}><Printer aria-hidden="true" />{t('property_arrears.print')}</Button> : null} />
  );
  if (!letter) return <div className="space-y-4 print:hidden">{header}<p className="text-muted-foreground">{t('property_arrears.none')}</p></div>;

  const missingText = (field: string) => {
    switch (field) {
      case 'postal_address': return t('property_arrears.missing_postal_address');
      case 'tenant_names': return t('property_arrears.missing_tenant_names');
      case 'landlord_name': return t('property_arrears.missing_landlord_name');
      default: return t('property_arrears.missing_landlord_address');
    }
  };
  const todo = t('property_arrears.todo');
  const lease = letter.missing.some((m) => LEASE_FIELDS.includes(m));
  const settings = letter.missing.includes('postal_address');
  const landlord = letter.landlordName || todo;
  const rows = view.months.filter((m) => m.inLetter);

  return (
    <div className="space-y-6">
      <div className="space-y-4 print:hidden">
        {header}
        <FilterRow label={t('common.filters')}>
          <FilterField label={t('property_arrears.days')}>
            {(labelId) => (
              <Segmented aria-labelledby={labelId} value={String(days)}
                options={DELAYS.map((n) => ({ value: String(n), label: t('property_arrears.days_option', { count: n }) }))}
                onValueChange={(next) => void navigate({ to: '/properties/$id/arrears-letter', params: { id }, search: { days: Number(next) } })} />
            )}
          </FilterField>
        </FilterRow>
        {letter.formerTenant ? <p className="text-sm text-muted-foreground">{t('property_arrears.former_tenant')}</p> : null}
        {letter.leavesOutEarlierTenant ? <p className="text-sm text-muted-foreground">{t('property_arrears.earlier_tenant')}</p> : null}
        {letter.missing.length > 0 ? (
          <div role="alert" className="flex gap-2 rounded-md border border-status-warning/40 bg-status-warning/10 p-3 text-sm text-status-warning-text">
            <AlertTriangle aria-hidden="true" className="mt-0.5 size-4 shrink-0" />
            <div className="space-y-1">
              <p>{t('property_arrears.missing')} {letter.missing.map(missingText).join(', ')}.</p>
              <p className="flex gap-4">
                {lease ? <Link to="/properties/$id" params={{ id }} search={{ tab: 'rental' }} className="underline">{t('property_arrears.fix_lease')}</Link> : null}
                {settings ? <Link to="/properties/$id" params={{ id }} search={{ tab: 'settings' }} className="underline">{t('property_arrears.fix_settings')}</Link> : null}
              </p>
            </div>
          </div>
        ) : null}
      </div>

      <article aria-labelledby={titleId} className="mx-auto max-w-2xl space-y-6 bg-white p-6 text-black print:max-w-none print:p-0">
        <p className="whitespace-pre-line">{landlord}{'\n'}{letter.landlordAddress || todo}</p>
        <p className="whitespace-pre-line">{letter.tenantNames || todo}{'\n'}{letter.postalAddress || todo}</p>
        <h2 id={titleId} className="text-2xl font-semibold">{t('property_arrears.title_letter')}</h2>
        <p>{t('property_arrears.greeting')}</p>
        <p>{t('property_arrears.intro', { address: letter.postalAddress || todo })}</p>
        <Table aria-label={t('property_arrears.table')} className="text-black">
          <TableHeader>
            <TableRow>
              <TableHead className="text-black">{t('property_arrears.month')}</TableHead>
              <TableHead className="text-right text-black">{t('property_arrears.due')}</TableHead>
              <TableHead className="text-right text-black">{t('property_arrears.paid')}</TableHead>
              <TableHead className="text-right text-black">{t('property_arrears.remaining')}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.map((m) => (
              <TableRow key={m.month}>
                <TableCell>{monthText(m.month)}</TableCell>
                <TableCell className="text-right tabular-nums">{money(view.currency, m.due)}</TableCell>
                <TableCell className="text-right tabular-nums">{money(view.currency, m.imputed)}</TableCell>
                <TableCell className="text-right tabular-nums">{money(view.currency, m.remaining)}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
        <p className="font-semibold tabular-nums">{t('property_arrears.grand_total', { total: money(view.currency, letter.total) })}</p>
        <p>{t('property_arrears.request', { deadline: formatLocalDate(deadlineIso(days)) })}</p>
        <p>{t('property_arrears.closing')}</p>
        <p>{t('property_arrears.made_on', { date: formatLocalDate(todayIso()) })}</p>
        <p>{landlord}</p>
      </article>
    </div>
  );
}
