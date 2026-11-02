import { useTranslation } from 'react-i18next';
import { formatMoney, type MoneyValue } from '@/components/Money';
import { SectionCard } from '@/components/page/SectionCard';
import { Table, TableBody, TableCell, TableHead, TableRow } from '@/components/ui/table';
import { NEUTRAL_COLOR } from '@/components/ProviderIcon';
import type { HouseholdHoldings, HoldingsExposure } from '../api';
import { regionText, sectorText, sharePct } from '../holdings/breakdown';
import { regionColor, sectorColor } from './colors';
import '@/locales/investments';

type Part = { key: string; label: string; color: string; value?: MoneyValue | null; share?: number | null };

/** « 0.00 » as the server writes a zero amount. */
const isZero = (value?: MoneyValue | null) => !value || /^-?0*(\.0*)?$/.test(value.amount);

/** Holdings exposure §5: the household's value by region and by sector from the server, each share a bar then a table; nothing computed here. */
export function ExposureSection({ d }: { d: HouseholdHoldings }) {
  const { t } = useTranslation();
  const e = d.exposure;
  const title = t('exposure.title');
  if (!e) {
    const codes = [...new Set(d.findings.filter((f) => f.code === 'core.fx_rate_missing').map((f) => (f.reference ?? '').split(':')[0]))].join(', ');
    return (
      <SectionCard title={title}>
        <p className="text-sm text-muted-foreground">{t('exposure.none')} {codes ? t('investments_holdings.unknown_rate', { codes }) : null}</p>
      </SectionCard>
    );
  }
  const rests = (withSectors: boolean): Part[] => [
    { key: 'outside', label: t('exposure.outside'), color: NEUTRAL_COLOR, ...restOf(e.outsideMarkets) },
    { key: 'unknown', label: t('exposure.unknown'), color: NEUTRAL_COLOR, ...restOf(e.unknown) },
    ...(withSectors ? [{ key: 'unclassified', label: t('exposure.unclassified'), color: NEUTRAL_COLOR, ...restOf(e.unclassifiedSectors) }] : []),
  ];
  const regions: Part[] = [
    ...e.regions.map((r) => ({ key: r.code, label: regionText(t, r.code), color: regionColor(r.code), value: r.value, share: r.sharePct })),
    ...rests(false),
  ].filter((p) => !isZero(p.value));
  const sectors: Part[] = [
    ...e.sectors.map((r) => ({ key: r.code, label: sectorText(t, r.code), color: sectorColor(r.code), value: r.value, share: r.sharePct })),
    ...rests(true),
  ].filter((p) => !isZero(p.value));
  // Every holding is unknown: no region, sector or market-less share carries anything.
  const allUnknown = !isZero(e.unknown.value) && e.regions.every((r) => isZero(r.value)) && isZero(e.outsideMarkets.value);
  return (
    <SectionCard title={title}>
      {allUnknown ? <p className="text-sm text-muted-foreground">{t('exposure.all_unknown')}</p> : null}
      <Bars label={t('exposure.regions')} caption={t('exposure.regions_table')} parts={regions} />
      <Bars label={t('exposure.sectors')} caption={t('exposure.sectors_table')} parts={sectors} />
      {e.staleValue && !isZero(e.staleValue) ? <p className="text-sm text-muted-foreground">{t('exposure.stale', { amount: formatMoney(e.staleValue) })}</p> : null}
    </SectionCard>
  );
}

const restOf = (r: HoldingsExposure['unknown']) => ({ value: r.value, share: r.sharePct });

/** dataviz.md §2: one stacked bar (2 px between fills), then the table the person reads with the labels. */
function Bars({ label, caption, parts }: { label: string; caption: string; parts: Part[] }) {
  const { t } = useTranslation();
  return (
    <div className="space-y-2">
      <p className="text-sm font-medium">{label}</p>
      <div aria-hidden="true" className="flex h-3 gap-0.5 overflow-hidden rounded-full bg-muted">
        {parts.map((p) => <span key={p.key} style={{ width: `${p.share ?? 0}%`, background: p.color }} />)}
      </div>
      <Table aria-label={caption}>
        <TableBody>
          {parts.map((p) => (
            <TableRow key={p.key}>
              <TableHead scope="row" className="h-auto py-1.5 font-normal text-foreground">
                <span className="inline-flex items-center gap-2">
                  <span aria-hidden="true" className="size-2.5 rounded-sm" style={{ background: p.color }} />{p.label}
                </span>
              </TableHead>
              <TableCell className="py-1.5 text-right tabular-nums">{p.value ? formatMoney(p.value) : null}</TableCell>
              <TableCell className="py-1.5 text-right tabular-nums text-muted-foreground">{p.share == null ? '' : t('investments.pct', { value: sharePct(p.share) })}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}
