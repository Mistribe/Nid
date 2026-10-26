import { Link } from '@tanstack/react-router';
import { AlertTriangle } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { EmptyState } from '@/components/EmptyState';
import { formatLocalDate } from '@/components/LocalDate';
import { Section } from '@/components/page/Section';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { downloadBlob } from '../export/download';
import { TAX_2044, type Rental2044 } from './api';
import { deficitLines, lineNote, lineText, noticeAmount, noticeText } from './format';
import { rental2044Csv } from './rental2044Csv';
import { eur } from './ResultTable';
import '@/locales/tax';

/**
 * Rental 2044 spec §6: what to copy into the form 2044, property by property, then the household's totals and the
 * expenses to classify. Every figure is the server's string; the CSV copies them as received.
 */
export function Rental2044Section({ year, scope }: { year: number; scope: 'me' | 'household' }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const answer = $api.useQuery('get', TAX_2044, { params: { query: { year, scope } } });
  const view = answer.data;
  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  if (!view) return <div aria-busy="true" aria-label={t('tax.loading')} className="h-40 shimmer rounded-md border bg-muted" />;
  const real = view.regime !== 'micro';
  if (real && view.properties.length === 0) return <EmptyState title={t('tax.d2044.empty_title')} body={t('tax.d2044.empty_body')} />;
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
      <Notices view={view} />
      {/* Under the micro-foncier nothing is deducted: the 4BE box and its note only. */}
      {real ? view.properties.map((p) => (
        <Section key={p.id} title={p.name}>
          <Lines lines={p.lines.map((l) => ({ code: l.line, amount: l.amount }))} />
        </Section>
      )) : null}
      <Section title={t('tax.d2044.totals')}>
        <Totals view={view} />
      </Section>
      {real ? <ToCheck view={view} /> : null}
    </>
  );
}

function Lines({ lines }: { lines: { code: string; amount: string }[] }) {
  const { t } = useTranslation();
  return (
    <dl className="grid grid-cols-[auto_1fr_auto] gap-x-4 gap-y-1 text-sm">
      {lines.map((l) => (
        <div key={l.code} className="col-span-3 grid grid-cols-subgrid items-baseline border-b py-1 last:border-b-0">
          <dt className="contents">
            <span className="font-mono text-muted-foreground">{l.code}</span>
            <span>
              {lineText(t, l.code)}
              {lineNote(t, l.code) ? <span className="block text-xs text-muted-foreground">{lineNote(t, l.code)}</span> : null}
            </span>
          </dt>
          <dd className="text-right font-medium tabular-nums">{eur(l.amount)}</dd>
        </div>
      ))}
    </dl>
  );
}

function Totals({ view }: { view: Rental2044 }) {
  const { t } = useTranslation();
  const { totals } = view;
  if (view.regime === 'micro') {
    return (
      <>
        <Lines lines={[{ code: '4BE', amount: totals.box4BE }]} />
        <p className="text-sm text-muted-foreground">{t('tax.d2044.micro_note')}</p>
      </>
    );
  }
  const [global, carried] = deficitLines(totals.interestDeficit);
  return (
    <Lines lines={[
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
      { label: t(deficit ? 'tax.d2044.figure.deficit' : 'tax.d2044.figure.result'), value: eur(totals.result), tone: deficit ? 'down' : undefined },
    ]} />
  );
}

function Notices({ view }: { view: Rental2044 }) {
  const { t } = useTranslation();
  if (!view.notices.length) return null;
  return (
    <div className="flex gap-3 rounded-md border border-status-warning/60 bg-status-warning/10 p-4">
      <AlertTriangle aria-hidden="true" className="mt-1 size-4 shrink-0 text-status-warning" />
      <Section title={t('tax.d2044.notes')}>
        <ul className="list-disc space-y-1 pl-5 text-sm">
          {view.notices.map((n, i) => <li key={`${n.code}-${n.item}-${i}`}>{noticeText(t, n.code, n.name, noticeAmount(t, n.code, n.amount, eur))}</li>)}
        </ul>
      </Section>
    </div>
  );
}

function ToCheck({ view }: { view: Rental2044 }) {
  const { t } = useTranslation();
  if (!view.toCheck.length) return null;
  return (
    <Section title={t('tax.d2044.to_check')}>
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
    </Section>
  );
}
