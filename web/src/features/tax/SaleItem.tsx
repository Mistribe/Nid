import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { formatLocalDate } from '@/components/LocalDate';
import { StatLine } from '@/components/page/StatLine';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { PERSONS } from '../subscriptions/api';
import type { SaleItemView } from './api';
import { ZERO, percentText } from './format';
import { eur } from './ResultTable';
import { feesMethodText, saleFindingText, saleFlagsText, saleNoteText, saleStatusText, worksMethodText } from './saleFormat';
import '@/locales/tax';

/** One item's sale estimate, the server's strings only; a finding says what is missing and shows no figure. */
export function SaleItem({ item, carriedForward, canWrite = false }: { item: SaleItemView; carriedForward: boolean; canWrite?: boolean }) {
  const { t } = useTranslation();
  const { property, envelope } = item;
  const figures = [
    [t('tax.sale.gain'), item.gain], [t('tax.sale.income_tax'), item.incomeTax], [t('tax.sale.social_tax'), item.socialTax],
    ...(item.surtax && !ZERO.test(item.surtax) ? [[t('tax.sale.surtax'), item.surtax]] : []), [t('tax.sale.net'), item.net],
  ].flatMap(([label, amount]) => (amount ? [{ label: label!, value: eur(amount) }] : []));
  const details = [
    property && [t('tax.sale.cost_price'), eur(property.costPrice)],
    property && [t('tax.sale.fees'), feesMethodText(t, property.feesMethod)],
    property && [t('tax.sale.works'), worksMethodText(t, property.worksMethod)],
    item.yearsHeld != null && [t('tax.sale.years_held'), item.yearsHeld],
    property && [t('tax.sale.allowances'), t('tax.sale.allowances_value', { income: percentText(t, property.incomeAllowancePct), social: percentText(t, property.socialAllowancePct) })],
    envelope && [t('tax.sale.remaining_capital'), eur(envelope.remainingCapital)],
    envelope?.gainBefore2017 && !ZERO.test(envelope.gainBefore2017) && [t('tax.sale.gain_before_2017'), eur(envelope.gainBefore2017)],
    envelope?.gainReduced && !ZERO.test(envelope.gainReduced) && [t('tax.sale.gain_reduced'), eur(envelope.gainReduced)],
    envelope?.gainFull && !ZERO.test(envelope.gainFull) && [t('tax.sale.gain_full'), eur(envelope.gainFull)],
    envelope?.allowanceUsed && !ZERO.test(envelope.allowanceUsed) && [t('tax.sale.allowance_used'), eur(envelope.allowanceUsed)],
  ].filter((d): d is [string, string | number] => Array.isArray(d));

  return (
    <div className="space-y-3">
      <p className="text-sm text-muted-foreground">{[t('tax.sale.estimate'), ...saleFlagsText(t, item), carriedForward ? t('tax.sale.carried') : null].filter(Boolean).join(' · ')}</p>
      {item.finding ? (
        <p className="text-sm">
          {saleFindingText(t, item.finding)}
          {canWrite && item.finding === 'opening_date_unknown' && item.module === 'investments' ? (
            <> <Link to="/investments/$id" params={{ id: item.item }} search={{ tab: 'settings' }} className="underline underline-offset-2">{t('tax.sale.fix_settings')}</Link></>
          ) : null}
        </p>
      ) : (
        <>
          <p className="text-sm font-medium">{saleStatusText(t, item.status)}</p>
          {item.value ? (
            <p className="text-sm">{item.valueOn ? t('tax.sale.value_on', { amount: eur(item.value), date: formatLocalDate(item.valueOn, 'short') }) : t('tax.sale.value', { amount: eur(item.value) })}</p>
          ) : null}
          {figures.length ? <StatLine items={figures} /> : null}
          {details.length ? (
            <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
              {details.map(([label, value]) => (
                <div key={label} className="col-span-2 grid grid-cols-subgrid">
                  <dt className="text-muted-foreground">{label}</dt>
                  <dd className="tabular-nums">{value}</dd>
                </div>
              ))}
            </dl>
          ) : null}
          {property && property.owners.length > 1 ? <Owners owners={property.owners} /> : null}
        </>
      )}
      {item.notes.length ? (
        <ul className="list-disc space-y-1 pl-5 text-sm text-muted-foreground">
          {item.notes.map((n) => <li key={n}>{saleNoteText(t, n)}</li>)}
        </ul>
      ) : null}
    </div>
  );
}

function Owners({ owners }: { owners: NonNullable<SaleItemView['property']>['owners'] }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const people = $api.useQuery('get', PERSONS).data;
  return (
    <Table aria-label={t('tax.sale.owners')}>
      <TableHeader>
        <TableRow>
          <TableHead>{t('tax.sale.owner')}</TableHead>
          <TableHead className="text-right">{t('tax.sale.gain')}</TableHead>
          <TableHead className="text-right">{t('tax.sale.income_tax')}</TableHead>
          <TableHead className="text-right">{t('tax.sale.social_tax')}</TableHead>
          <TableHead className="text-right">{t('tax.sale.surtax')}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {owners.map((o) => (
          <TableRow key={o.person}>
            <TableCell>{people?.find((p) => p.id === o.person)?.displayName ?? t('tax.sale.owner')}</TableCell>
            <TableCell className="text-right tabular-nums">{eur(o.gain)}</TableCell>
            <TableCell className="text-right tabular-nums">{eur(o.incomeTax)}</TableCell>
            <TableCell className="text-right tabular-nums">{eur(o.socialTax)}</TableCell>
            <TableCell className="text-right tabular-nums">{eur(o.surtax)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
