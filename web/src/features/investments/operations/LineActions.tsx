import type { TFunction } from 'i18next';
import { lazy, Suspense, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import { ConfirmFromMenu } from '@/components/ConfirmAction';
import { RowMenu } from '@/components/page/RowMenu';
import { ProblemMessage } from '@/components/ProblemMessage';
import { formatCurrency } from '@/components/Money';
import { type BankTransaction } from '@/api/banking';
import { useLineActions } from './api';
import '@/locales/banks';

const LinkDialog = lazy(() => import('./LinkDialog'));
const CategoryDialog = lazy(() => import('./CategoryDialog'));
const NoteDialog = lazy(() => import('./NoteDialog'));
const SplitDialog = lazy(() => import('./SplitDialog'));

/** What a line says about its link: « À relier », « Reliée · Netflix », « Reliée » when the target is not the reader's to see… */
export function lineState(t: TFunction, l: BankTransaction): string {
  if (l.linkState === 'linked') return l.link?.itemName ? t('banks.operations.linked_to', { name: l.link.itemName }) : t('banks.operations.linked');
  if (l.linkState === 'internal') return l.link?.counterpartName ? t('banks.operations.internal_to', { name: l.link.counterpartName }) : t('banks.operations.internal');
  return t(l.linkState === 'ignored' ? 'banks.operations.ignored' : 'banks.operations.unlinked');
}

/** A line's `⋯` menu: « Relier… » and « Ignorer » on an unlinked line, « Délier » on a linked or ignored one, « Catégoriser… » on any but an ignored one; a refusal shows under it. */
export function LineActions({ line, onDone }: { line: BankTransaction; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { unlink, ignore, unsplit } = useLineActions();
  const [linking, setLinking] = useState(false);
  const [categorising, setCategorising] = useState(false);
  const [noting, setNoting] = useState(false);
  const [splitting, setSplitting] = useState(false);
  const [unsplitting, setUnsplitting] = useState(false);
  const menu = useRef<HTMLButtonElement>(null);
  const run = (action: typeof unlink, message: string) => () => action.mutate({ params: { path: { id: line.id } } }, { onSuccess: () => onDone(message) });
  const items = line.linkState === 'unlinked'
    ? [{ label: t('banks.operations.link'), onSelect: () => setLinking(true) }, { label: t('banks.operations.ignore'), onSelect: run(ignore, t('banks.operations.done_ignore')) }]
    : [{ label: t('banks.operations.unlink'), onSelect: run(unlink, t('banks.operations.done_unlink')) }];
  const isSplit = !!line.parts?.length;
  // A split line has no category of its own (the server answers 409 `banking.line_split`).
  if (line.linkState !== 'ignored' && !isSplit) items.push({ label: t('banks.operations.categorise'), onSelect: () => setCategorising(true) });
  items.push({ label: t('banks.operations.note'), onSelect: () => setNoting(true) });
  items.push(isSplit ? { label: t('banks.operations.unsplit'), onSelect: () => setUnsplitting(true) } : { label: t('banks.operations.split'), onSelect: () => setSplitting(true) });
  const error = unlink.error ?? ignore.error ?? unsplit.error;
  return (
    <span className="flex flex-col items-end gap-2">
      <RowMenu label={t('banks.operations.actions')} items={items} triggerRef={menu} />
      {unsplitting ? <ConfirmFromMenu label={t('banks.operations.unsplit')} confirmation={t('banks.operations.unsplit_confirm')} pending={unsplit.isPending} returnFocus={menu}
        onClose={() => setUnsplitting(false)} onConfirm={run(unsplit, t('banks.operations.done_unsplit'))} /> : null}
      {error ? <ProblemMessage problem={present(toProblem(error))} /> : null}
      {linking ? <Suspense fallback={null}><LinkDialog line={line} onClose={() => setLinking(false)} onDone={(m) => { setLinking(false); onDone(m); }} /></Suspense> : null}
      {categorising ? <Suspense fallback={null}><CategoryDialog line={line} onClose={() => setCategorising(false)} onDone={(m) => { setCategorising(false); onDone(m); }} /></Suspense> : null}
      {noting ? <Suspense fallback={null}><NoteDialog line={line} onClose={() => setNoting(false)} onDone={(m) => { setNoting(false); onDone(m); }} /></Suspense> : null}
      {splitting ? <Suspense fallback={null}><SplitDialog line={line} onClose={() => setSplitting(false)} onDone={(m) => { setSplitting(false); onDone(m); }} /></Suspense> : null}
    </span>
  );
}

/** A split line's category cell: « Découpée (n) », then each part (amount · category · note) under it; `null` for a whole line. */
export function SplitParts({ line }: { line: BankTransaction }) {
  const { t } = useTranslation();
  if (!line.parts?.length) return null;
  return (
    <span className="whitespace-normal">
      {t('banks.operations.split_count', { count: line.parts.length })}
      <ul className="text-sm text-muted-foreground">
        {line.parts.map((p) => (
          <li key={p.id}>{[formatCurrency(p.amount, line.currency), p.category?.name ?? t('banks.categories.uncategorised'), p.note].filter(Boolean).join(' · ')}</li>
        ))}
      </ul>
    </span>
  );
}
