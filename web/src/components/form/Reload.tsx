import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import '@/locales/forms';

/** On a 409 the record changed meanwhile: read it again rather than overwrite (screens.md §10). `onReload` re-reads it. */
export function Reload({ onReload }: { onReload: () => Promise<void> | void }) {
  const { t } = useTranslation();
  return <Button type="button" variant="outline" onClick={() => void onReload()}>{t('edit.reload')}</Button>;
}
