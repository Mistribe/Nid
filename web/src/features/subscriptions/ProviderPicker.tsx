import { useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { ComboboxField } from '@/components/form/ComboboxField';
import { Field } from '@/components/form/Field';
import { ProblemMessage } from '@/components/ProblemMessage';
import { ProviderIcon } from '@/components/ProviderIcon';
import { Button } from '@/components/ui/button';
import { PROVIDERS } from '@/api/subscriptions';
import { NewProviderDialog } from './NewProviderDialog';
import '@/locales/forms';

export type ChosenProvider = { key: string; name: string } | null;

/** A searchable list of the providers the reader can see (1C2b's rule, on the server), or a custom one created on the spot (1E2b2). */
export function ProviderPicker({ value, onChange, label, error }: {
  value: ChosenProvider; onChange: (provider: ChosenProvider) => void; label: string; error?: string;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const root = useRef<HTMLDivElement>(null);
  const [creating, setCreating] = useState(false);
  const providers = $api.useQuery('get', PROVIDERS, { params: { query: {} } });
  const options = useMemo(() => {
    const listed = (providers.data ?? []).map((p) => ({
      value: p.key, label: p.name,
      icon: <ProviderIcon name={p.name} iconUrl={p.iconUrl} color={p.labels[0]?.color} className="size-5 text-[0.6rem]" />,
    }));
    // The one just created may not be in the list yet.
    const known = !value || listed.some((o) => o.value === value.key) ? listed : [{ value: value.key, label: value.name }, ...listed];
    return [{ value: '', label: t('form.none') }, ...known];
  }, [providers.data, value, t]);
  const names = new Map(options.map((o) => [o.value, o.label]));
  const focusPicker = () => root.current?.querySelector<HTMLElement>('[role="combobox"]')?.focus();

  return (
    <div ref={root} className="space-y-2">
      <Field label={label} hint={t('edit.provider_hint')} error={error}>
        {(c) => (
          <ComboboxField {...c} value={value?.key ?? ''} options={options} searchPlaceholder={t('form.search')} empty={providers.isPending ? t('form.loading') : t('form.no_result')}
            onValueChange={(key) => onChange(key === '' ? null : { key, name: names.get(key) ?? key })} />
        )}
      </Field>
      {providers.error ? <ProblemMessage problem={present(toProblem(providers.error))} /> : null}
      {/* Without the list, a provider that exists could not be seen: creating one would duplicate it. */}
      <Button type="button" variant="ghost" size="sm" disabled={!!providers.error} onClick={() => setCreating(true)}>{t('edit.provider_new')}</Button>
      {creating ? (
        <NewProviderDialog
          onClose={() => { setCreating(false); focusPicker(); }}
          onCreated={(provider) => { setCreating(false); onChange(provider); focusPicker(); }} />
      ) : null}
    </div>
  );
}
