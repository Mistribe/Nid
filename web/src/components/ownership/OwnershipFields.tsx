import { PlusIcon, XIcon } from 'lucide-react';
import { useId } from 'react';
import { Controller, useFieldArray, type ArrayPath, type FieldArray, type FieldErrors, type Path, type UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import type { components } from '@/api/schema';
import { Segmented } from '@/components/form/Segmented';
import { SelectField } from '@/components/form/SelectField';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Field } from '@/components/form/Field';
import type { OwnershipValues } from './ownership';
import '@/locales/forms';

type Person = components['schemas']['PersonDto'];

/**
 * The subscription wizard's visibility-and-owners block, shared with any other form whose values extend
 * `OwnershipValues` (e.g. the envelope form). RHF types `register`/`useFieldArray` against the concrete form
 * type, not a generic bound by an interface, so the literal `visibility` / `owners` field names below are cast
 * to `Path<T>` / `ArrayPath<T>`: safe because every `T extends OwnershipValues` has those fields, but not
 * something RHF's generics can see on their own.
 *
 * The income form asks its own visibility question (2C2a design §5) and passes `withVisibility={false}`.
 */
export function OwnershipFields<T extends OwnershipValues>({ form, persons, withVisibility = true }: { form: UseFormReturn<T>; persons: Person[]; withVisibility?: boolean }) {
  const { t } = useTranslation();
  const owners = useFieldArray({ control: form.control, name: 'owners' as ArrayPath<T> });
  // Same reason as above: `errors.owners` / `errors.visibility` exist on every `T extends OwnershipValues`, but
  // RHF's `FieldErrors<T>` for a generic, unresolved `T` can't express that, so this narrows to the concrete shape.
  const e = form.formState.errors as FieldErrors<OwnershipValues>;
  const ownersError = e.owners?.message ?? e.owners?.root?.message;
  const visibilityId = useId();
  const effectId = useId();
  return (
    <>
      {withVisibility ? (
        <div className="space-y-1">
          <span id={visibilityId} className="block text-sm">{t('wizard.visibility')}</span>
          <Controller control={form.control} name={'visibility' as Path<T>} render={({ field }) => (
            <Segmented aria-labelledby={visibilityId} aria-describedby={effectId} value={field.value as string} onValueChange={field.onChange}
              options={(['shared', 'private'] as const).map((v) => ({ value: v, label: t(`wizard.${v}`) }))} />
          )} />
          <p id={effectId} className="text-sm text-muted-foreground">
            {form.watch('visibility' as Path<T>) === 'private' ? t('wizard.private_effect') : t('wizard.shared_effect')}
          </p>
        </div>
      ) : null}
      <fieldset className="space-y-2">
        <legend className="text-sm">{t('wizard.owners')}</legend>
        {owners.fields.map((row, i) => (
          <div key={row.id} className="grid grid-cols-[minmax(0,1fr)_6rem_auto] items-end gap-2">
            <Controller control={form.control} name={`owners.${i}.personId` as Path<T>} render={({ field }) => (
              <Field label={t('wizard.owner')} error={e.owners?.[i]?.personId?.message}>
                {(c) => <SelectField {...c} value={field.value as string} onValueChange={field.onChange}
                  options={persons.map((p) => ({ value: p.id, label: p.displayName }))} />}
              </Field>
            )} />
            <Field label={t('wizard.share')} error={e.owners?.[i]?.share?.message}>
              {(c) => <Input inputMode="decimal" {...c} {...form.register(`owners.${i}.share` as Path<T>)} />}
            </Field>
            {owners.fields.length > 1 ? (
              <Button type="button" variant="ghost" size="icon" aria-label={t('wizard.remove_owner')} onClick={() => owners.remove(i)}><XIcon aria-hidden="true" /></Button>
            ) : <span />}
          </div>
        ))}
        {ownersError ? <p role="alert" className="text-sm text-destructive">{ownersError}</p> : null}
        <Button type="button" variant="ghost" size="sm" onClick={() => owners.append({ personId: persons[0]?.id ?? '', share: '' } as FieldArray<T, ArrayPath<T>>)}>
          <PlusIcon aria-hidden="true" />{t('wizard.add_owner')}
        </Button>
      </fieldset>
    </>
  );
}
