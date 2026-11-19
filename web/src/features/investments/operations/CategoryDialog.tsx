import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import { FormDialog } from '@/components/FormDialog';
import { ComboboxField } from '@/components/form/ComboboxField';
import { Field } from '@/components/form/Field';
import { formatCurrency } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { useCategories, type BankTransaction } from '@/api/banking';
import { useLineActions } from './api';
import '@/locales/banks';

// Not category ids (those are uuids): the two choices that are no category.
const NONE = 'none';
const AUTO = 'auto';

/** A line's category by hand: an active category (« Parent › Enfant », a parent alone too), « Sans catégorie », or back to Nid's own choice. */
export default function CategoryDialog({ line, onClose, onDone }: { line: BankTransaction; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { categorise, resetCategory } = useLineActions();
  const categories = useCategories();
  const [picked, setPicked] = useState(line.category?.id ?? (line.categorySource === 'hand' ? NONE : ''));
  const active = (categories.data ?? []).filter((c) => !c.archived && !c.mergedIntoId);
  const options = [
    { value: NONE, label: t('banks.categories.uncategorised') },
    { value: AUTO, label: t('banks.categories.auto') },
    ...active.filter((p) => !p.parentId).flatMap((p) => [
      { value: p.id, label: p.name },
      ...active.filter((c) => c.parentId === p.id).map((c) => ({ value: c.id, label: `${p.name} › ${c.name}` })),
    ]),
  ];
  // The line's own category may be archived by now: not on the list, so not sent back as is.
  const chosen = options.some((o) => o.value === picked);
  const gone = categories.data && line.category && picked === line.category.id && !chosen ? line.category.name : null;
  // Looking at a link's or a rule's category must not pin it as a hand choice; a hand « Sans catégorie » is already pinned.
  const unchanged = line.categorySource === 'hand' ? !line.category && picked === NONE : picked === line.category?.id;
  const problem = categories.error ?? categorise.error ?? resetCategory.error;

  return (
    <FormDialog title={t('banks.categories.title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-4" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          const path = { params: { path: { id: line.id } } };
          const then = { onSuccess: () => done(t('banks.operations.done_category')) };
          if (picked === AUTO) resetCategory.mutate(path, then);
          else if (chosen) categorise.mutate({ ...path, body: { categoryId: picked === NONE ? null : picked } }, then);
        }}>
          <p className="text-sm text-muted-foreground">{line.label} · {formatCurrency(line.amount, line.currency)}</p>
          {problem ? <ProblemMessage problem={present(toProblem(problem))} /> : null}
          <Field label={t('banks.categories.field')} hint={gone ? t('banks.categories.current_gone', { name: gone }) : undefined}>
            {(c) => <ComboboxField {...c} value={picked} onValueChange={setPicked} options={options} placeholder={t('banks.categories.pick')} searchPlaceholder={t('banks.categories.search')} empty={t('banks.categories.none_found')} />}
          </Field>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={!chosen || unchanged || categorise.isPending || resetCategory.isPending}>{t('banks.categories.submit')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}
