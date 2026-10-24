import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import { invalidateSubscriptions } from '../api';
import '@/locales/forms';

/**
 * On a 409 the record changed meanwhile: read it again rather than overwrite (screens.md §10). Defaults to
 * reloading the subscription; a caller elsewhere (investments) passes its own `onReload`.
 */
export function Reload({ onReload }: { onReload?: () => Promise<void> | void } = {}) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const reload = onReload ?? (() => invalidateSubscriptions(queryClient));
  return <Button type="button" variant="outline" onClick={() => void reload()}>{t('edit.reload')}</Button>;
}
