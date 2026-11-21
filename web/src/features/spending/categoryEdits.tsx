import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ConfirmAction } from '@/components/ConfirmAction';
import { FormDialog } from '@/components/FormDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { ComboboxField } from '@/components/form/ComboboxField';
import { Field } from '@/components/form/Field';
import { fieldErrorsOf } from '@/components/form/fieldErrors';
import type { Option } from '@/components/form/options';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { bankErrorText, CATEGORIES, CATEGORY, CATEGORY_MERGE, presentBankError, TRANSACTIONS, type BankCategory } from '@/api/banking';
import { SPENDING } from './api';
import '@/locales/banks';
import { mergeTargets } from './categoryTargets';

/** The writes on the list; a new name or a merge shows on the bank lines and the spending figures too. */
export function useCategoryWrites() {
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const onSuccess = () => Promise.all([CATEGORIES, TRANSACTIONS, SPENDING].map((path) => queryClient.invalidateQueries({ queryKey: ['get', path] })));
  return {
    create: $api.useMutation('post', CATEGORIES, { onSuccess }),
    update: $api.useMutation('patch', CATEGORY, { onSuccess }),
    merge: $api.useMutation('post', CATEGORY_MERGE, { onSuccess }),
  };
}

/** The `name` field's refusal under the field, any other above the form. */
export function useNameError(error: unknown) {
  const { t } = useTranslation();
  const field = fieldErrorsOf(error).find((f) => f.path === 'name');
  return {
    name: field ? bankErrorText(t, field.code, field.message) : undefined,
    problem: error && !field ? <ProblemMessage problem={presentBankError(t, error)} /> : null,
  };
}

export function Buttons({ cancel, submit, pending }: { cancel: () => void; submit: string; pending: boolean }) {
  const { t } = useTranslation();
  return (
    <div className="flex justify-end gap-2">
      <Button type="button" variant="outline" onClick={cancel}>{t('common.cancel')}</Button>
      <Button type="submit" disabled={pending}>{submit}</Button>
    </div>
  );
}

export function RenameDialog({ category, onClose, onDone }: { category: BankCategory; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { update } = useCategoryWrites();
  const [name, setName] = useState(category.name);
  const errors = useNameError(update.error);
  return (
    <FormDialog title={t('banks.category_list.rename_title', { name: category.name })} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-4" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          update.mutate({ params: { path: { id: category.id } }, body: { name: name.trim() } }, { onSuccess: () => done(t('banks.category_list.done_rename')) });
        }}>
          {errors.problem}
          <Field label={t('banks.category_list.name')} error={errors.name}>
            {(c) => <Input {...c} value={name} maxLength={60} onChange={(event) => setName(event.target.value)} />}
          </Field>
          <Buttons cancel={() => done()} submit={t('banks.category_list.save')} pending={update.isPending} />
        </form>
      )}
    </FormDialog>
  );
}

export function MergeDialog({ category, targets, onClose, onDone }: {
  category: BankCategory; targets: Option[]; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { merge } = useCategoryWrites();
  const [into, setInto] = useState('');
  const target = targets.find((o) => o.value === into);
  return (
    <FormDialog title={t('banks.category_list.merge_title', { name: category.name })} onClose={onClose} onDone={onDone}>
      {({ done }) => (
        <div className="space-y-4">
          {merge.error ? <ProblemMessage problem={presentBankError(t, merge.error)} /> : null}
          <Field label={t('banks.category_list.merge_into')}>
            {(c) => <ComboboxField {...c} value={into} onValueChange={setInto} options={targets} placeholder={t('banks.categories.pick')}
              searchPlaceholder={t('banks.categories.search')} empty={t('banks.categories.none_found')} />}
          </Field>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <ConfirmAction label={t('banks.category_list.merge_submit')} disabled={!target} pending={merge.isPending}
              confirmation={t('banks.category_list.merge_confirm', { name: category.name, into: target?.label ?? '' })}
              onConfirm={() => merge.mutate({ params: { path: { id: category.id } }, body: { into } }, { onSuccess: () => done(t('banks.category_list.done_merge')) })} />
          </div>
        </div>
      )}
    </FormDialog>
  );
}

/** A category's rename or merge from wherever it shows (#415: the spending page), by id, once the list is loaded. */
export function CategoryEdit({ edit, categories, onClose, onDone }: {
  edit: { mode: 'rename' | 'merge'; id: string }; categories: BankCategory[]; onClose: () => void; onDone: (message: string) => void;
}) {
  const category = categories.find((c) => c.id === edit.id);
  if (!category) return null;
  return edit.mode === 'rename'
    ? <RenameDialog category={category} onClose={onClose} onDone={onDone} />
    : <MergeDialog category={category} targets={mergeTargets(categories, category)} onClose={onClose} onDone={onDone} />;
}
