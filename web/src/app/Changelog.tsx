import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { formatLocalDate } from '@/components/LocalDate';
import { ModalDialog } from '@/components/ModalDialog';
import { Button } from '@/components/ui/button';

type Text = Record<string, string>;
type Version = { version: string; date: string; changes: Text[] };

/**
 * public/changelog.json, written by scripts/changelog.mjs at image build. A dev server has none: it answers with the SPA,
 * which is no list, so the dialog says there is nothing to show.
 */
async function fetchChangelog(): Promise<Version[]> {
  const response = await fetch('/changelog.json', { headers: { Accept: 'application/json' } });
  if (!response.ok || !response.headers.get('Content-Type')?.includes('json')) return [];
  const body: unknown = await response.json();
  return Array.isArray(body) ? (body as Version[]) : [];
}

/** The versions and what changed in each, in the app's language (French when a change has no line in it). */
export function Changelog({ onClose }: { onClose: () => void }) {
  const { t, i18n } = useTranslation();
  const list = useQuery({ queryKey: ['changelog'], queryFn: fetchChangelog, staleTime: Infinity });
  const line = (text: Text) => text[i18n.language] ?? text.fr ?? Object.values(text)[0];

  return (
    <ModalDialog title={t('app.changelog.title')} onClose={onClose}>
      {() => (
        <>
          <div className="-mx-6 max-h-[60vh] overflow-y-auto px-6">
            {list.isPending ? <p className="text-sm text-muted-foreground">{t('app.changelog.loading')}</p>
              : list.isError ? <p className="text-sm text-muted-foreground">{t('app.changelog.error')}</p>
              : list.data.length === 0 ? <p className="text-sm text-muted-foreground">{t('app.changelog.empty')}</p>
              : (
                <ol className="space-y-5">
                  {list.data.map((v) => (
                    <li key={v.version}>
                      <h3 className="flex items-baseline gap-2 text-sm">
                        <span className="font-semibold">{t('app.version', { version: v.version })}</span>
                        <time dateTime={v.date} className="text-xs text-muted-foreground">{formatLocalDate(v.date, 'long', i18n.language)}</time>
                      </h3>
                      <ul className="mt-1.5 list-disc space-y-1 pl-5 text-sm">
                        {v.changes.map((change, i) => <li key={i}>{line(change)}</li>)}
                      </ul>
                    </li>
                  ))}
                </ol>
              )}
          </div>
          <div className="flex justify-end">
            <Button onClick={onClose}>{t('app.changelog.close')}</Button>
          </div>
        </>
      )}
    </ModalDialog>
  );
}
