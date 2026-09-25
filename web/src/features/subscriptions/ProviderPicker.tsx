import { useEffect, useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { Button } from '@/components/ui/button';
import { ProviderIcon } from '@/components/ProviderIcon';
import { PROVIDERS } from './api';

export type ChosenProvider = { key: string; name: string } | null;

/** Searches the providers the reader can see (1C2b's rule, on the server) and returns the one chosen. */
export function ProviderPicker({ value, onChange, label, error }: {
  value: ChosenProvider; onChange: (provider: ChosenProvider) => void; label: string; error?: string;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const id = useId();
  const hintId = useId();
  const errorId = useId();
  const [typed, setTyped] = useState('');
  const [q, setQ] = useState('');
  useEffect(() => {
    const timer = setTimeout(() => setQ(typed.trim()), 250);
    return () => clearTimeout(timer);
  }, [typed]);
  const found = $api.useQuery('get', PROVIDERS, { params: { query: { q } } }, { enabled: q !== '' });

  return (
    <div className="space-y-1">
      <label htmlFor={id} className="block text-sm">{label}</label>
      {value ? (
        <p className="flex items-center gap-2">
          <span>{t('edit.provider_chosen', { name: value.name })}</span>
          <Button type="button" size="sm" variant="ghost" onClick={() => onChange(null)}>{t('edit.provider_clear')}</Button>
        </p>
      ) : null}
      <input id={id} type="search" value={typed} onChange={(e) => setTyped(e.target.value)} className="w-full rounded-md border px-3 py-2"
        aria-describedby={error ? `${hintId} ${errorId}` : hintId} {...(error ? { 'aria-invalid': true } : {})} />
      <span id={hintId} className="block text-sm text-muted-foreground">{t('edit.provider_hint')}</span>
      {error ? <span id={errorId} className="block text-sm text-destructive">{error}</span> : null}
      {q !== '' && found.data ? (
        found.data.length === 0 ? <p className="text-sm">{t('edit.provider_none')}</p> : (
          <ul className="space-y-1">
            {found.data.slice(0, 8).map((p) => (
              <li key={p.key}>
                <Button type="button" variant="outline" size="sm" className="gap-2"
                  aria-label={t('edit.provider_choose', { name: p.name })}
                  onClick={() => { onChange({ key: p.key, name: p.name }); setTyped(''); setQ(''); }}>
                  <ProviderIcon name={p.name} iconUrl={p.iconUrl} color={p.labels[0]?.color} className="size-5" />
                  {p.name}
                </Button>
              </li>
            ))}
          </ul>
        )
      ) : null}
    </div>
  );
}
