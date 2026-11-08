import { PlusIcon } from 'lucide-react';
import { useRef } from 'react';
import { Controller, useFieldArray, type UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ComboboxField } from '@/components/form/ComboboxField';
import { Field } from '@/components/form/Field';
import { Segmented } from '@/components/form/Segmented';
import type { Option } from '@/components/form/options';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { ENVELOPES } from '@/api/investments';
import type { GoalValues } from './goalSchema';
import '@/locales/goals';

/** The open envelopes the author can fund a goal from, plus those the goal already names (a closed one may be kept). */
export function useEnvelopeOptions(kept: { envelopeId: string; name: string }[] = []): Option[] {
  const { $api } = useApi();
  const envelopes = $api.useQuery('get', ENVELOPES, { params: { query: { scope: 'me' } } });
  const open = (envelopes.data?.items ?? []).filter((e) => !e.closed).map((e) => ({ value: e.id, label: e.displayName }));
  return [...open, ...kept.filter((k) => !open.some((o) => o.value === k.envelopeId)).map((k) => ({ value: k.envelopeId, label: k.name }))];
}

/** « Enveloppes » (rows of an envelope and its share) or « Libre » (no envelope: dated readings on the goal). */
export function FundingFields({ form, options }: { form: UseFormReturn<GoalValues>; options: Option[] }) {
  const { t } = useTranslation();
  const rows = useFieldArray({ control: form.control, name: 'funding' });
  const addRef = useRef<HTMLButtonElement>(null);
  const e = form.formState.errors;
  const empty = () => ({ envelopeId: '', share: '100' });

  return (
    <div className="space-y-3">
      <div className="space-y-1">
        <span id="goal-mode" className="block text-sm">{t('goals_form.mode')}</span>
        <Controller control={form.control} name="mode" render={({ field }) => (
          <Segmented aria-labelledby="goal-mode" value={field.value}
            onValueChange={(v) => { field.onChange(v); if (v === 'envelopes' && rows.fields.length === 0) rows.append(empty()); }}
            options={(['envelopes', 'free'] as const).map((m) => ({ value: m, label: t(`goals_form.mode_${m}`) }))} />
        )} />
      </div>
      {form.watch('mode') === 'free' ? <p className="text-sm text-muted-foreground">{t('goals_form.free_help')}</p> : (
        <>
          {e.funding?.message ?? e.funding?.root?.message ? <p role="alert" className="text-sm text-destructive">{e.funding?.message ?? e.funding?.root?.message}</p> : null}
          {rows.fields.map((row, i) => (
            <fieldset key={row.id} className="grid gap-3 rounded-md border p-3 sm:grid-cols-[minmax(0,1fr)_8rem_auto] sm:items-end">
              <legend className="px-1 text-sm font-medium">{t('goals_form.envelope_n', { n: i + 1 })}</legend>
              <Controller control={form.control} name={`funding.${i}.envelopeId`} render={({ field }) => (
                <Field label={t('goals_form.envelope')} error={e.funding?.[i]?.envelopeId?.message}>
                  {(c) => <ComboboxField {...c} value={field.value} onValueChange={field.onChange} options={options}
                    placeholder={t('goals_form.envelope_placeholder')} searchPlaceholder={t('goals_form.search')} empty={t('goals_form.no_envelope')} />}
                </Field>
              )} />
              <Field label={t('goals_form.share')} error={e.funding?.[i]?.share?.message}>
                {(c) => <Input inputMode="decimal" {...c} {...form.register(`funding.${i}.share`)} />}
              </Field>
              <Button type="button" variant="ghost" size="sm" onClick={() => { rows.remove(i); addRef.current?.focus(); }}>
                {t('goals_form.remove_envelope', { n: i + 1 })}
              </Button>
            </fieldset>
          ))}
          <Button ref={addRef} type="button" variant="outline" size="sm" onClick={() => rows.append(empty())}>
            <PlusIcon aria-hidden="true" />{t('goals_form.add_envelope')}
          </Button>
        </>
      )}
    </div>
  );
}
