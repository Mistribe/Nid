import type { TFunction } from 'i18next';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { Section } from '@/components/page/Section';
import { StatLine } from '@/components/page/StatLine';
import { LocalDate } from '@/components/LocalDate';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { money, percent, signedMoney } from '../../loans/format';
import { PROPERTY_RETURN, type PropertyDetail, type PropertyReturn } from '@/api/properties';
import { AssumptionsDialog } from './AssumptionsDialog';
import '@/locales/loans';
import '@/locales/properties';

/** An IRR of the API, already in percent (`6.2`), shown with a decimal comma: text only. */
const irrText = (t: TFunction, irr: string | null | undefined): string =>
  irr ? t('loans.percent', { rate: irr.replace('.', ',') }) : t('property_return.none');

/**
 * Property return spec §6: the household's scenario in figures, the assumptions it rests on, its detail by line and the return
 * of a resale each year from today. Every figure is the server's.
 */
export function ReturnSection({ property }: { property: PropertyDetail }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const [editing, setEditing] = useState(false);
  const query = $api.useQuery('get', PROPERTY_RETURN, { params: { path: { id: property.id } } });

  if (query.isPending) return <p className="text-sm text-muted-foreground">{t('property_return.loading')}</p>;
  if (query.isError) return <ProblemMessage problem={present(toProblem(query.error))} />;
  const r = query.data;
  const scenario = r.scenario;

  return (
    <Section title={t('property_return.title')}>
      {scenario ? (
        <>
          <p>{t('property_return.scenario', { year: scenario.outcome.resaleOn.slice(0, 4), price: money(property.currency, scenario.outcome.resalePrice) })}</p>
          <StatLine items={[
            { label: t('property_return.irr'), value: irrText(t, scenario.outcome.irr) },
            { label: t('property_return.net_gain'), value: signedMoney(property.currency, scenario.outcome.netGain) },
            { label: t('property_return.savings_effort'), value: money(property.currency, scenario.savingsEffort) },
            { label: t('property_return.irr_today'), value: irrText(t, r.sensitivity[0]?.irr) },
          ]} />
        </>
      ) : null}
      {property.canWrite ? <Button variant="outline" onClick={() => setEditing(true)}>{t('property_return.edit')}</Button> : null}
      <Assumptions r={r} />
      {scenario ? <Breakdown currency={property.currency} lines={scenario.breakdown} /> : null}
      {r.sensitivity.length > 0 ? <Sensitivity currency={property.currency} r={r} /> : null}
      {r.warnings.length > 0 ? (
        <ul className="text-sm text-status-warning-text">
          {r.warnings.map((w) => <li key={`${w.code}-${w.loanId ?? ''}`}>{t(`property_return.warnings.${w.code}`, { defaultValue: w.code })}</li>)}
        </ul>
      ) : null}
      <p className="text-sm text-muted-foreground">{t('property_return.disclaimer')}</p>
      {editing ? <AssumptionsDialog property={property} assumptions={r.assumptions} onClose={() => setEditing(false)} /> : null}
    </Section>
  );
}

function Assumptions({ r }: { r: PropertyReturn }) {
  const { t } = useTranslation();
  const a = r.assumptions;
  return (
    <p className="text-sm text-muted-foreground">
      {t('property_return.assumptions', {
        value: percent(t, a.valueGrowth), rent: percent(t, a.rentGrowth), charges: percent(t, a.chargesGrowth), vacancy: a.vacancyMonths,
        marginal: percent(t, a.marginalRate), fees: percent(t, a.resaleFees),
      })}
    </p>
  );
}

function Breakdown({ currency, lines }: { currency: string; lines: NonNullable<PropertyReturn['scenario']>['breakdown'] }) {
  const { t } = useTranslation();
  return (
    <Table aria-label={t('property_return.breakdown')}>
      <TableHeader>
        <TableRow>
          <TableHead>{t('property_return.line')}</TableHead>
          <TableHead className="text-right">{t('property_return.realized')}</TableHead>
          <TableHead className="text-right">{t('property_return.projected')}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {lines.map((l) => (
          <TableRow key={l.line}>
            <TableCell>{t(`property_return.lines.${l.line}`, { defaultValue: l.line })}</TableCell>
            <TableCell className="text-right tabular-nums">{signedMoney(currency, l.realized)}</TableCell>
            <TableCell className="text-right tabular-nums">{signedMoney(currency, l.projected)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

function Sensitivity({ currency, r }: { currency: string; r: PropertyReturn }) {
  const { t } = useTranslation();
  const scenario = r.scenario?.outcome;
  const pinelYear = r.pinelEnd?.slice(0, 4);
  // The scenario's resale rarely falls on a resale year's date: it is then a row of its own, in date order.
  const rows = scenario && !r.sensitivity.some((row) => row.resaleOn === scenario.resaleOn)
    ? [...r.sensitivity, scenario].sort((a, b) => a.resaleOn.localeCompare(b.resaleOn))
    : r.sensitivity;
  return (
    <Table aria-label={t('property_return.sensitivity')}>
      <TableHeader>
        <TableRow>
          <TableHead>{t('property_return.date')}</TableHead>
          <TableHead className="text-right">{t('property_return.price')}</TableHead>
          <TableHead className="text-right">{t('property_return.irr')}</TableHead>
          <TableHead className="text-right">{t('property_return.net_gain')}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map((row) => {
          const year = row.resaleOn.slice(0, 4);
          const current = row.resaleOn === scenario?.resaleOn;
          return (
            <TableRow key={row.resaleOn} aria-current={current ? 'true' : undefined} className={current ? 'bg-accent' : undefined}>
              <TableCell>
                <LocalDate value={row.resaleOn} />
                {year === pinelYear ? <Badge variant="secondary" className="ml-2">{t('property_return.pinel_end')}</Badge> : null}
                {row.pinelClawback ? <span className="ml-2 text-sm text-muted-foreground">{t('property_return.clawback')}</span> : null}
              </TableCell>
              <TableCell className="text-right tabular-nums">{money(currency, row.resalePrice)}</TableCell>
              <TableCell className="text-right tabular-nums">{irrText(t, row.irr)}</TableCell>
              <TableCell className="text-right tabular-nums">{signedMoney(currency, row.netGain)}</TableCell>
            </TableRow>
          );
        })}
      </TableBody>
    </Table>
  );
}
