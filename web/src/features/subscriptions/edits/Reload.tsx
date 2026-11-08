import { useQueryClient } from '@tanstack/react-query';
import { Reload as ReloadButton } from '@/components/form/Reload';
import { invalidateSubscriptions } from '../api';

/** The shared `Reload`, reading the subscription again unless the caller passes its own `onReload`. */
export function Reload({ onReload }: { onReload?: () => Promise<void> | void } = {}) {
  const queryClient = useQueryClient();
  return <ReloadButton onReload={onReload ?? (() => invalidateSubscriptions(queryClient))} />;
}
