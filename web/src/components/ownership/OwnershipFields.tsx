import { useId } from 'react';
import { useFieldArray, type ArrayPath, type FieldArray, type FieldErrors, type Path, type UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import type { components } from '@/api/schema';
import { Button } from '@/components/ui/button';
import { Field } from '@/features/subscriptions/edits/Field';
import type { OwnershipValues } from './ownership';

type Person = components['schemas']['PersonDto'];
const input = 'rounded-md border px-3 py-2';

/**
 * The subscription wizard's visibility-and-owners block, shared with any other form whose values extend
 * `OwnershipValues` (e.g. the envelope form). RHF types `register`/`useFieldArray` against the concrete form
 * type, not a generic bound by an interface, so the literal `visibility` / `owners` field names below are cast
 * to `Path<T>` / `ArrayPath<T>`: safe because every `T extends OwnershipValues` has those fields, but not
 * something RHF's generics can see on their own.
 */
export function OwnershipFields<T extends OwnershipValues>({ form, persons }: { form: UseFormReturn<T>; persons: Person[] }) {
  const { t } = useTranslation();
  const owners = useFieldArray({ control: form.control, name: 'owners' as ArrayPath<T> });
  // Same reason as above: `errors.owners` / `errors.visibility` exist on every `T extends OwnershipValues`, but
  // RHF's `FieldErrors<T>` for a generic, unresolved `T` can't express that, so this narrows to the concrete shape.
  const e = form.formState.errors as FieldErrors<OwnershipValues>;
  const ownersError = e.owners?.message ?? e.owners?.root?.message;
  const visibilityId = useId();
  return (
    <>
      <fieldset aria-describedby={visibilityId} className="space-y-1">
        <legend className="text-sm">{t('wizard.visibility')}</legend>
        {(['shared', 'private'] as const).map((v) => (
          <label key={v} className="flex items-center gap-2">
            <input type="radio" value={v} {...form.register('visibility' as Path<T>)} />
            {t(`wizard.${v}`)}
          </label>
        ))}
        <p id={visibilityId} className="text-sm text-muted-foreground">
          {form.watch('visibility' as Path<T>) === 'private' ? t('wizard.private_effect') : t('wizard.shared_effect')}
        </p>
      </fieldset>
      <fieldset className="space-y-2">
        <legend className="text-sm">{t('wizard.owners')}</legend>
        {owners.fields.map((row, i) => (
          <div key={row.id} className="flex flex-wrap items-end gap-2">
            <Field label={t('wizard.owner')} error={e.owners?.[i]?.personId?.message}>
              {(c) => (
                <select {...c} {...form.register(`owners.${i}.personId` as Path<T>)} className={input}>
                  {persons.map((p) => <option key={p.id} value={p.id}>{p.displayName}</option>)}
                </select>
              )}
            </Field>
            <Field label={t('wizard.share')} error={e.owners?.[i]?.share?.message}>
              {(c) => <input inputMode="decimal" {...c} {...form.register(`owners.${i}.share` as Path<T>)} className={`w-24 ${input}`} />}
            </Field>
            {owners.fields.length > 1 ? (
              <Button type="button" variant="ghost" size="sm" onClick={() => owners.remove(i)}>{t('wizard.remove_owner')}</Button>
            ) : null}
          </div>
        ))}
        {ownersError ? <p role="alert" className="text-sm text-destructive">{ownersError}</p> : null}
        <Button type="button" variant="outline" size="sm" onClick={() => owners.append({ personId: persons[0]?.id ?? '', share: '' } as FieldArray<T, ArrayPath<T>>)}>
          {t('wizard.add_owner')}
        </Button>
      </fieldset>
    </>
  );
}
