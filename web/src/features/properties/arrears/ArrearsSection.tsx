import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { Section } from '@/components/page/Section';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { money } from '../../loans/format';
import { PROPERTY_ARREARS } from '../api';
import { monthText } from '../format';
import '@/locales/properties';

/**
 * Rent arrears: the months still due, oldest first (a changeover month once per tenant), overdue or still running, with the
 * server's figures; the overdue total and the reminder letter only when the server built one. Nothing at all while no month is listed.
 */
export function ArrearsSection({ propertyId }: { propertyId: string }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const answer = $api.useQuery('get', PROPERTY_ARREARS, { params: { path: { id: propertyId } } });
  const view = answer.data;
  if (!view || view.months.length === 0) return null;
  return (
    <Section title={t('property_arrears.title')}>
      <p className="text-sm tabular-nums">
        <span className="font-medium">{t('property_arrears.total', { amount: money(view.currency, view.total) })}</span>
        {view.letter ? <>{' · '}{t('property_arrears.overdue_total', { amount: money(view.currency, view.overdueTotal) })}</> : null}
      </p>
      <Table aria-label={t('property_arrears.table')}>
        <TableHeader>
          <TableRow>
            <TableHead>{t('property_arrears.month')}</TableHead>
            <TableHead className="text-right">{t('property_arrears.due')}</TableHead>
            <TableHead className="text-right">{t('property_arrears.paid')}</TableHead>
            <TableHead className="text-right">{t('property_arrears.remaining')}</TableHead>
            <TableHead>{t('property_arrears.status')}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {view.months.map((m, i) => (
            // A changeover month is one row per tenant: the month alone is no key.
            <TableRow key={`${m.month}-${i}`}>
              <TableCell>{monthText(m.month)}</TableCell>
              <TableCell className="text-right tabular-nums">{money(view.currency, m.due)}</TableCell>
              <TableCell className="text-right tabular-nums">{money(view.currency, m.imputed)}</TableCell>
              <TableCell className="text-right tabular-nums">{money(view.currency, m.remaining)}</TableCell>
              <TableCell className={m.overdue ? 'font-medium text-status-warning-text' : 'text-muted-foreground'}>
                {m.overdue ? t('property_arrears.overdue') : t('property_arrears.pending')}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      {view.letter ? (
        <Link to="/properties/$id/arrears-letter" params={{ id: propertyId }} search={{}} className="text-sm underline">{t('property_arrears.letter')}</Link>
      ) : null}
    </Section>
  );
}
