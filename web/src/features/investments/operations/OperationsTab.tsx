import { lazy, Suspense, useEffect, useRef, useState } from 'react';
import { useNavigate, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { present, toProblem } from '@/api/errors';
import { DateField } from '@/components/form/DateField';
import { LocalDate } from '@/components/LocalDate';
import { InfoTip } from '@/components/InfoTip';
import { formatCurrency } from '@/components/Money';
import { FilterField, FilterRow } from '@/components/page/FilterRow';
import { Section } from '@/components/page/Section';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import type { EnvelopeDetail } from '@/api/investments';
import type { EnvelopeSearch } from '@/app/router';
import { type BankTransaction } from '@/api/banking';
import { useTransactions } from './api';
import ImportStatementDialog from './ImportStatementDialog';
import { LineActions, lineState, SplitParts } from './LineActions';
import { Receipts } from './Receipts';
import '@/locales/banks';

const ISO = /^\d{4}-\d{2}-\d{2}$/;
const CategoryDialog = lazy(() => import('./CategoryDialog'));
const NONE: ReadonlySet<string> = new Set();

/** Bank sync spec §6: the booked lines of the envelope's bank account and its cards, newest first, page by page, and « Importer un relevé ». */
export default function OperationsTab({ envelope, writable }: { envelope: EnvelopeDetail; writable: boolean }) {
  const { t } = useTranslation();
  // #642: the period lives in the URL (reload, shared link); the field keeps what is half typed.
  const search: EnvelopeSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const [from, setFrom] = useState(search.from ?? '');
  const [to, setTo] = useState(search.to ?? '');
  const bound = (key: 'from' | 'to', set: (text: string) => void) => (text: string) => {
    set(text);
    void navigate({ to: '.', replace: true, resetScroll: false, search: ((prev: EnvelopeSearch) => ({ ...prev, [key]: ISO.test(text) ? text : undefined })) as never });
  };
  const [importing, setImporting] = useState(false);
  const [landOn, setLandOn] = useState(false);
  const importRef = useRef<HTMLButtonElement>(null);
  // A half-typed date asks nothing: only a whole one bounds the period.
  const filtered = ISO.test(from) || ISO.test(to);
  const lines = useTransactions(envelope.id, ISO.test(from) ? from : undefined, ISO.test(to) ? to : undefined);
  const rows = lines.data?.pages.flatMap((p) => p.items) ?? [];
  // #435: the lines picked for « Catégoriser… », of the period shown: another period starts with none.
  const period = `${ISO.test(from) ? from : ''}|${ISO.test(to) ? to : ''}`;
  const [picked, setPicked] = useState<{ of: string; ids: ReadonlySet<string> }>({ of: period, ids: NONE });
  const selected = picked.of === period ? picked.ids : NONE;
  const [categorising, setCategorising] = useState(false);
  const chosen = rows.filter((l) => selected.has(l.id));
  // As « Catégoriser… » in a row's menu: neither split nor ignored.
  const categorisable = (l: BankTransaction) => !l.parts?.length && l.linkState !== 'ignored';
  // Once the dialog handed itself back, the focus returns to its button, never <body>.
  useEffect(() => { if (landOn) { importRef.current?.focus(); setLandOn(false); } }, [landOn]);

  const label = (l: BankTransaction) => (
    <span className="whitespace-normal">
      <span className="flex flex-wrap items-center gap-2">{l.label}<Receipts line={l} />{l.fromCard ? <Badge variant="secondary">{t('banks.operations.card')}</Badge> : null}</span>
      {l.counterparty || l.cardName ? (
        <span className="block text-sm text-muted-foreground">{[l.counterparty, l.cardName].filter(Boolean).join(' · ')}</span>
      ) : null}
      {l.note ? <span className="block text-sm italic text-muted-foreground">{l.note}</span> : null}
    </span>
  );
  // The category, then where it comes from in a muted word explained by its « i ».
  const category = (l: BankTransaction) => {
    if (l.parts?.length) return <SplitParts line={l} />;
    if (!l.category && l.categorySource !== 'hand') return '—';
    const source = l.categorySource ? t(`banks.categories.source.${l.categorySource}`, { defaultValue: l.categorySource }) : null;
    return (
      <span className="inline-flex flex-wrap items-center gap-1.5">
        {l.category?.name ?? t('banks.categories.uncategorised')}
        {source ? (
          <span className="inline-flex items-center gap-1 text-xs text-muted-foreground">
            {source}
            <InfoTip label={source}>{t(`banks.categories.source_about.${l.categorySource}`, { defaultValue: source })}</InfoTip>
          </span>
        ) : null}
      </span>
    );
  };
  const actions = (l: BankTransaction) => <LineActions line={l} onDone={toast.success} />;
  const amount = (l: BankTransaction) => <span className="tabular-nums">{formatCurrency(l.amount, l.currency)}</span>;

  return (
    <Section title={t('banks.operations.title')} srTitle>
      <div className="flex flex-wrap items-center gap-3">
        <FilterRow label={t('common.filters')}>
          <FilterField label={t('banks.operations.from')} labelFor>
            {(labelId, controlId) => <div className="w-44"><DateField id={controlId} aria-labelledby={labelId} value={from} onChange={bound('from', setFrom)} /></div>}
          </FilterField>
          <FilterField label={t('banks.operations.to')} labelFor>
            {(labelId, controlId) => <div className="w-44"><DateField id={controlId} aria-labelledby={labelId} value={to} onChange={bound('to', setTo)} /></div>}
          </FilterField>
        </FilterRow>
        {writable ? (
          <div className="ml-auto flex flex-wrap gap-2">
            {chosen.length > 0 ? <Button type="button" onClick={() => setCategorising(true)}>{t('banks.operations.categorise_many', { count: chosen.length })}</Button> : null}
            <Button ref={importRef} type="button" variant="outline" onClick={() => setImporting(true)}>{t('banks.import.open')}</Button>
          </div>
        ) : null}
      </div>
      {lines.error ? <ProblemMessage problem={present(toProblem(lines.error))} /> : null}
      {lines.isPending ? <div role="status" aria-busy="true" aria-label={t('banks.operations.loading')} className="h-40 shimmer rounded-md border bg-muted" /> : null}
      {lines.data && rows.length === 0 ? <p className="text-muted-foreground">{t(filtered ? 'banks.operations.empty_period' : 'banks.operations.empty')}</p> : null}
      {rows.length > 0 ? (
        <SortableTable<BankTransaction> caption={t('banks.operations.title')} rows={rows} rowKey={(l) => l.id}
          selection={writable ? { selected, onChange: (ids) => setPicked({ of: period, ids }), label: (l) => t('banks.operations.select', { label: l.label }),
            allLabel: t('banks.operations.select_page'), disabled: (l) => !categorisable(l) } : undefined}
          columns={[
            { id: 'date', header: t('banks.operations.date'), sort: (l) => l.bookedOn, cell: (l) => <LocalDate value={l.bookedOn} style="short" /> },
            { id: 'label', header: t('banks.operations.label'), cell: label },
            { id: 'category', header: t('banks.categories.column'), sort: (l) => l.category?.name ?? '', cell: category },
            { id: 'amount', header: t('banks.operations.amount'), align: 'end', sort: (l) => amountKey(l.amount), cell: amount },
            { id: 'state', header: t('banks.operations.state'), sort: (l) => lineState(t, l), cell: (l) => lineState(t, l) },
            ...(writable ? [{ id: 'actions', header: t('banks.operations.actions_col'), align: 'end' as const, cell: actions }] : []),
          ]}
          mobile={(l) => (
            <div className="space-y-1">
              <div className="flex items-start justify-between gap-2">{label(l)}{amount(l)}</div>
              <div className="flex items-center justify-between gap-2 text-sm text-muted-foreground">
                <span><LocalDate value={l.bookedOn} style="short" />{l.category || l.parts?.length || l.categorySource === 'hand' ? <> · {category(l)}</> : null} · {lineState(t, l)}</span>
                {writable ? actions(l) : null}
              </div>
            </div>
          )} />
      ) : null}
      {lines.hasNextPage ? (
        <Button type="button" variant="outline" disabled={lines.isFetchingNextPage} onClick={() => void lines.fetchNextPage()}>{t('common.more')}</Button>
      ) : null}
      {categorising && chosen.length > 0 ? (
        <Suspense fallback={null}>
          <CategoryDialog lines={chosen} onClose={() => setCategorising(false)} onDone={(message) => { setCategorising(false); setPicked({ of: period, ids: NONE }); toast.success(message); }} />
        </Suspense>
      ) : null}
      {importing ? (
        <ImportStatementDialog envelopeId={envelope.id} onClose={() => setImporting(false)}
          onDone={(message) => { setImporting(false); toast.success(message); setLandOn(true); }} />
      ) : null}
    </Section>
  );
}
