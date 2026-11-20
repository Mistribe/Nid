import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import { FormDialog } from '@/components/FormDialog';
import { ComboboxField } from '@/components/form/ComboboxField';
import { Field } from '@/components/form/Field';
import { fieldErrorsOf, messageFor } from '@/components/form/fieldErrors';
import { formatCurrency } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { activeCategoryOptions, useCategories, type BankTransaction } from '@/api/banking';
import { useLineActions } from './api';
import '@/locales/banks';
import '@/locales/forms';

const NONE = 'none';
// The typed text only: digits, one separator, up to two decimals.
const AMOUNT = /^\d+([.,]\d{1,2})?$/;
const MIN_PARTS = 2;
const MAX_PARTS = 20;

type Part = { key: number; amount: string; category: string; note: string };

/**
 * « Découper »: a line into 2 to 20 parts, typed positive and sent with the line's sign. The dialog adds nothing up: the server
 * answers `banking.split_sum` with the two sums, shown on the parts.
 */
export default function SplitDialog({ line, onClose, onDone }: { line: BankTransaction; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { split } = useLineActions();
  const categories = useCategories();
  const next = useRef(0);
  const blank = (): Part => ({ key: next.current++, amount: '', category: NONE, note: '' });
  const [parts, setParts] = useState<Part[]>(() => line.parts?.length
    ? line.parts.map((p) => ({ key: next.current++, amount: p.amount.replace(/^-/, ''), category: p.category?.id ?? NONE, note: p.note ?? '' }))
    : [blank(), blank()]);
  const sign = line.amount.startsWith('-') ? '-' : '';
  const options = [{ value: NONE, label: t('banks.categories.uncategorised') }, ...activeCategoryOptions(categories.data)];
  const change = (key: number, over: Partial<Part>) => setParts((all) => all.map((p) => (p.key === key ? { ...p, ...over } : p)));
  const errors = fieldErrorsOf(split.error);
  const onParts = errors.find((f) => f.path === 'parts');
  const [checked, setChecked] = useState(false);
  const bad = (p: Part) => checked && !AMOUNT.test(p.amount.trim());

  return (
    <FormDialog title={t('banks.operations.split_title')} className="sm:max-w-2xl" onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-4" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          setChecked(true);
          if (!parts.every((p) => AMOUNT.test(p.amount.trim()))) return;
          split.mutate({
            params: { path: { id: line.id } },
            body: { parts: parts.map((p) => ({ amount: `${sign}${p.amount.trim().replace(',', '.')}`, categoryId: p.category === NONE ? null : p.category, note: p.note.trim() || null })) },
          }, { onSuccess: () => done(t('banks.operations.done_split')) });
        }}>
          <p className="text-sm text-muted-foreground">{line.label} · {formatCurrency(line.amount, line.currency)}</p>
          {split.error && !onParts ? <ProblemMessage problem={present(toProblem(split.error))} /> : null}
          <ul className="space-y-3">
            {parts.map((p, i) => (
              <li key={p.key} className="grid gap-2 sm:grid-cols-[8rem_1fr_1fr_auto] sm:items-end">
                <Field label={t('banks.operations.part_amount', { n: i + 1 })} error={bad(p) ? t('banks.operations.part_amount_invalid') : undefined}>
                  {(c) => <Input {...c} inputMode="decimal" value={p.amount} onChange={(event) => change(p.key, { amount: event.target.value })} />}
                </Field>
                <Field label={t('banks.operations.part_category', { n: i + 1 })}>
                  {(c) => <ComboboxField {...c} value={p.category} onValueChange={(v) => change(p.key, { category: v })} options={options}
                    placeholder={t('banks.categories.pick')} searchPlaceholder={t('banks.categories.search')} empty={t('banks.categories.none_found')} />}
                </Field>
                <Field label={t('banks.operations.part_note', { n: i + 1 })}>
                  {(c) => <Input {...c} maxLength={500} value={p.note} onChange={(event) => change(p.key, { note: event.target.value })} />}
                </Field>
                <Button type="button" variant="ghost" disabled={parts.length <= MIN_PARTS} onClick={() => setParts((all) => all.filter((x) => x.key !== p.key))}>
                  {t('banks.operations.part_remove', { n: i + 1 })}
                </Button>
              </li>
            ))}
          </ul>
          {onParts ? <p role="alert" className="text-sm text-destructive">{messageFor(t, onParts)}</p> : null}
          <Button type="button" variant="outline" disabled={parts.length >= MAX_PARTS} onClick={() => setParts((all) => [...all, blank()])}>{t('banks.operations.part_add')}</Button>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={split.isPending}>{t('banks.operations.split_submit')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}
