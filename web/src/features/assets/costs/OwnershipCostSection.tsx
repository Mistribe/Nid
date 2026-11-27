import { Link } from '@tanstack/react-router';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ASSET_OWNERSHIP_COST, type AssetView, type CostPart } from '@/api/assets';
import { present, toProblem } from '@/api/errors';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { InfoTip } from '@/components/InfoTip';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { categoryText } from './cost';
import '@/locales/assets';

const LAST_12 = 'last12';

/**
 * #650: what the vehicle costs a month over a period (the server's figures), part by part. A part Nid has no figure for says so and
 * points to where it is filled in; it is left out of the total, which then says it is partial.
 */
export function OwnershipCostSection({ asset, onTab }: { asset: AssetView; onTab: (tab: 'subscriptions' | 'valuations') => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const [year, setYear] = useState<number | undefined>();
  const answer = $api.useQuery('get', ASSET_OWNERSHIP_COST, { params: { path: { id: asset.id }, query: { year } } });
  const cost = answer.data;

  const label = (p: CostPart) => p.code.startsWith('running:') ? categoryText(t, p.code.slice('running:'.length))
    : p.code === 'subscription' ? (p.sourceId && p.name ? <Link to="/subscriptions/$id" params={{ id: p.sourceId }} className="underline">{p.name}</Link> : t('assets_ownership.part.subscription'))
      : p.code === 'credit' ? (p.sourceId && p.name ? <>{t('assets_ownership.part.credit')} · <Link to="/loans/$id" params={{ id: p.sourceId }} className="underline">{p.name}</Link></> : t('assets_ownership.part.credit'))
        : t('assets_ownership.part.depreciation');
  const missing = (p: CostPart) => p.code === 'subscription' && !p.sourceId
    ? <Button type="button" variant="link" className="h-auto p-0" onClick={() => onTab('subscriptions')}>{t('assets_ownership.missing_subscription')}</Button>
    : p.code === 'depreciation'
      ? <Button type="button" variant="link" className="h-auto p-0" onClick={() => onTab('valuations')}>{t('assets_ownership.missing_value')}</Button>
      : p.code === 'running:fuel' ? t('assets_ownership.missing_fuel') : t('assets_ownership.missing_rate');

  return (
    <section aria-labelledby="ownership-title" className="space-y-3">
      <h3 id="ownership-title" className="flex items-center gap-1 font-medium">
        {t('assets_ownership.title')}
        <InfoTip label={t('assets_ownership.title')}>{t('assets_ownership.tip')}</InfoTip>
      </h3>
      {answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} /> : null}
      {cost ? (
        <>
          <div className="flex flex-wrap items-end gap-3">
            <div className="w-44">
              <Field label={t('assets_ownership.period')}>
                {(c) => <SelectField {...c} value={year === undefined ? LAST_12 : String(year)} onValueChange={(v) => setYear(v === LAST_12 ? undefined : Number(v))}
                  options={[{ value: LAST_12, label: t('assets_ownership.last_12') }, ...cost.years.map((y) => ({ value: String(y), label: String(y) }))]} />}
              </Field>
            </div>
            <p className="text-sm">
              <strong className="text-lg tabular-nums"><Money value={cost.monthly} /></strong> {t('assets_ownership.a_month')}
              {cost.perKm ? <> · <span className="tabular-nums"><Money value={cost.perKm} /></span> {t('assets_ownership.per_km')}</> : null}
            </p>
            <p className="text-sm text-muted-foreground">
              <LocalDate value={cost.from} /> – <LocalDate value={cost.to} /> · {t('assets_ownership.total')} <Money value={cost.total} />
            </p>
          </div>
          {cost.incomplete ? <p className="text-sm text-status-warning-text">{t('assets_ownership.incomplete')}</p> : null}
          <Table aria-label={t('assets_ownership.table')}>
            <TableHeader>
              <TableRow>
                <TableHead>{t('assets_ownership.col_part')}</TableHead>
                <TableHead className="text-right">{t('assets_ownership.col_amount')}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {cost.parts.map((p, i) => (
                <TableRow key={`${p.code}-${p.sourceId ?? i}`}>
                  <TableCell className="whitespace-normal">{label(p)}</TableCell>
                  <TableCell className="text-right">
                    {p.amount ? <span className="tabular-nums"><Money value={p.amount} /></span> : <span className="text-muted-foreground">{t('assets_ownership.missing')} · {missing(p)}</span>}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </>
      ) : null}
    </section>
  );
}
