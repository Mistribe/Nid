import { Link } from '@tanstack/react-router';
import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { EmptyState } from '@/components/EmptyState';
import { InfoTip } from '@/components/InfoTip';
import { formatLocalDate } from '@/components/LocalDate';
import { SectionCard } from '@/components/page/SectionCard';
import { StatLine } from '@/components/page/StatLine';
import { Loading, SectionCardSkeleton, StatLineSkeleton, TableSkeleton } from '@/components/page/skeletons';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { downloadBlob } from '@/lib/download';
import { TAX_2044, type Rental2044 } from '@/api/tax';
import { deficitLines, lineNote, lineText, tipText } from '../format';
import { rental2044Csv } from '../rental2044Csv';
import { eur } from '../ResultTable';
import { TaxNotices } from '../TaxNotices';
import { BoxList } from './BoxList';
import '@/locales/tax';

/**
 * Rental 2044 spec §6: what to copy into the form 2044, property by property, then the household's totals and the
 * expenses to classify. Every figure is the server's string; the CSV copies them as received.
 */
export function Rental2044Form({ year, scope }: { year: number; scope: 'me' | 'household' }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const answer = $api.useQuery('get', TAX_2044, { params: { query: { year, scope } } });
  const view = answer.data;
  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  if (!view) {
    return (
      <Loading label={t('tax.loading')}>
        <StatLineSkeleton items={4} />
        <SectionCardSkeleton><TableSkeleton columns={3} rows={4} /></SectionCardSkeleton>
      </Loading>
    );
  }
  const real = view.regime !== 'micro';
  if (real && view.properties.length === 0) return <EmptyState module="/tax" title={t('tax.d2044.empty_title')} body={t('tax.d2044.empty_body')} />;
  return (
    <>
      <div className="flex flex-wrap items-center gap-2 print:hidden">
        <Button type="button" variant="outline" onClick={() => downloadBlob(new Blob([rental2044Csv(view, t)], { type: 'text/csv' }), `declaration-2044-${view.year}.csv`)}>
          {t('tax.d2044.csv')}
        </Button>
        <Button type="button" variant="outline" onClick={() => window.print()}>{t('tax.d2044.print')}</Button>
      </div>
      {real ? <Figures view={view} /> : null}
      {view.provisional ? <p className="text-sm text-muted-foreground">{t('tax.d2044.provisional')}</p> : null}
      {view.notices.length ? <div className="print:hidden"><TaxNotices notices={view.notices} year={view.year} /></div> : null}
      {/* Under the micro-foncier nothing is deducted: the 4BE box and its note only. */}
      {real ? view.properties.map((p) => (
        <SectionCard key={p.id} title={p.name}>
          <Lines label={p.name} lines={p.lines.map((l) => ({ code: l.line, amount: l.amount }))} />
        </SectionCard>
      )) : null}
      <SectionCard title={t('tax.d2044.totals')}>
        <Totals view={view} />
      </SectionCard>
      {real ? <ToCheck view={view} /> : null}
    </>
  );
}

/** The lines whose tax term needs an explanation: the deficits, the taxable result, the micro-foncier's box. */
function lineTip(t: TFunction, code: string): string | undefined {
  if (['435', '440'].includes(code)) return tipText(t, 'rental_deficit');
  if (['438', '441'].includes(code)) return tipText(t, 'deficit_carried');
  if (code === '4BA') return tipText(t, 'rental');
  if (code === '4BE') return t('tax.d2044.micro_tip');
  return undefined;
}

function Lines({ label, lines }: { label: string; lines: { code: string; amount: string }[] }) {
  const { t } = useTranslation();
  return (
    <BoxList label={label} rows={lines.map((l) => ({
      code: l.code, title: lineText(t, l.code), tip: lineTip(t, l.code), note: lineNote(t, l.code) || undefined, amount: l.amount,
    }))} />
  );
}

function Totals({ view }: { view: Rental2044 }) {
  const { t } = useTranslation();
  const { totals } = view;
  if (view.regime === 'micro') {
    return (
      <>
        <Lines label={t('tax.d2044.totals')} lines={[{ code: '4BE', amount: totals.box4BE }]} />
        <p className="flex items-center gap-1.5 text-sm text-muted-foreground">{t('tax.d2044.micro_note')}<InfoTip label={t('tax.d2044.micro_note')}>{t('tax.d2044.micro_tip')}</InfoTip></p>
      </>
    );
  }
  const [global, carried] = deficitLines(totals.interestDeficit);
  return (
    <Lines label={t('tax.d2044.totals')} lines={[
      { code: '420', amount: totals.result }, { code: global, amount: totals.globalDeficit },
      { code: carried, amount: totals.carriedDeficit }, { code: '4BA', amount: totals.box4BA },
    ]} />
  );
}

function Figures({ view }: { view: Rental2044 }) {
  const { t } = useTranslation();
  const { totals } = view;
  const deficit = totals.result.startsWith('-');
  return (
    <StatLine items={[
      { label: t('tax.d2044.figure.receipts'), value: eur(totals.receipts) },
      { label: t('tax.d2044.figure.charges'), value: eur(totals.charges) },
      { label: t('tax.d2044.figure.interest'), value: eur(totals.interest) },
      { label: t(deficit ? 'tax.d2044.figure.deficit' : 'tax.d2044.figure.result'), value: eur(deficit ? totals.result.slice(1) : totals.result) },
    ]} />
  );
}

function ToCheck({ view }: { view: Rental2044 }) {
  const { t } = useTranslation();
  if (!view.toCheck.length) return null;
  return (
    <SectionCard title={t('tax.d2044.to_check')}>
      <p className="text-sm text-muted-foreground">{t('tax.d2044.to_check_help')}</p>
      <ul className="divide-y text-sm">
        {view.toCheck.map((c, i) => (
          <li key={`${c.property}-${c.occurredOn}-${i}`} className="flex flex-wrap items-baseline gap-x-4 gap-y-1 py-2">
            <Link to="/properties/$id" params={{ id: c.property }} search={{ tab: 'expenses' }} className="font-medium underline-offset-2 hover:underline">{c.name}</Link>
            <span className="text-muted-foreground">{formatLocalDate(c.occurredOn, 'short')}</span>
            <span>{t(`property_flows.category_option.${c.category}`, { defaultValue: c.category })}</span>
            {c.note ? <span className="text-muted-foreground">{c.note}</span> : null}
            <span className="ml-auto font-medium tabular-nums">{eur(c.amount)}</span>
          </li>
        ))}
      </ul>
    </SectionCard>
  );
}
