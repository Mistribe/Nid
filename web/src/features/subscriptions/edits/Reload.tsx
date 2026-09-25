import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import { invalidateSubscriptions } from '../api';

/** On a 409 the subscription changed meanwhile: read it again rather than overwrite (screens.md §10). */
export function Reload() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  return <Button type="button" variant="outline" onClick={() => void invalidateSubscriptions(queryClient)}>{t('edit.reload')}</Button>;
}
