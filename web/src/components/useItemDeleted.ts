import { useQueryClient } from '@tanstack/react-query';
import { useNavigate, type LinkProps } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import '@/locales/deletion';

/**
 * After an item delete (#23): the toast, the module's list, then every query. Leave first: the item's own queries would refetch
 * into a 404. Every query: the item may show on any screen (dashboard, documents, bank links).
 */
export function useItemDeleted(list: LinkProps['to']) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  return (name: string) => {
    toast.success(t('deletion.done', { name }));
    void navigate({ to: list }).then(() => queryClient.invalidateQueries());
  };
}
