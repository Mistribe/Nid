import type { TFunction } from 'i18next';
import { lazy, Suspense, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import { RowMenu } from '@/components/page/RowMenu';
import { ProblemMessage } from '@/components/ProblemMessage';
import { useLineActions, type BankTransaction } from './api';
import '@/locales/banks';

const LinkDialog = lazy(() => import('./LinkDialog'));

/** What a line says about its link: « À relier », « Reliée · Netflix », « Reliée » when the target is not the reader's to see… */
export function lineState(t: TFunction, l: BankTransaction): string {
  if (l.linkState === 'linked') return l.link?.itemName ? t('banks.operations.linked_to', { name: l.link.itemName }) : t('banks.operations.linked');
  if (l.linkState === 'internal') return l.link?.counterpartName ? t('banks.operations.internal_to', { name: l.link.counterpartName }) : t('banks.operations.internal');
  return t(l.linkState === 'ignored' ? 'banks.operations.ignored' : 'banks.operations.unlinked');
}

/** A line's `⋯` menu: « Relier… » and « Ignorer » on an unlinked line, « Délier » on a linked or ignored one; a refusal shows under it. */
export function LineActions({ line, onDone }: { line: BankTransaction; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { unlink, ignore } = useLineActions();
  const [linking, setLinking] = useState(false);
  const run = (action: typeof unlink, message: string) => () => action.mutate({ params: { path: { id: line.id } } }, { onSuccess: () => onDone(message) });
  const items = line.linkState === 'unlinked'
    ? [{ label: t('banks.operations.link'), onSelect: () => setLinking(true) }, { label: t('banks.operations.ignore'), onSelect: run(ignore, t('banks.operations.done_ignore')) }]
    : [{ label: t('banks.operations.unlink'), onSelect: run(unlink, t('banks.operations.done_unlink')) }];
  const error = unlink.error ?? ignore.error;
  return (
    <span className="flex flex-col items-end gap-2">
      <RowMenu label={t('banks.operations.actions')} items={items} />
      {error ? <ProblemMessage problem={present(toProblem(error))} /> : null}
      {linking ? <Suspense fallback={null}><LinkDialog line={line} onClose={() => setLinking(false)} onDone={(m) => { setLinking(false); onDone(m); }} /></Suspense> : null}
    </span>
  );
}
