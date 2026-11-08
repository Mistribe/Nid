import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ConfirmAction } from '@/components/ConfirmAction';
import { formatLocalDate } from '@/components/LocalDate';
import { Badge } from '@/components/ui/badge';
import { HOLDINGS, PRICE_ACCEPT, type Holding } from '@/api/investments';
import { formatUnits } from './holdingFormat';
import '@/locales/investments';

/** The latest price as the server found it; a refused one (a jump over 20 %) waits for the person's say. */
export function PriceCell({ envelope, holding, writable }: { envelope: string; holding: Holding; writable: boolean }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const accept = $api.useMutation('post', PRICE_ACCEPT, { onSettled: () => queryClient.invalidateQueries({ queryKey: ['get', HOLDINGS] }) });
  const price = holding.price;
  if (!price) return null;
  return (
    <div className="space-y-1">
      <p className="tabular-nums">{formatUnits(price.value)} {price.currency}</p>
      <p className="text-xs text-muted-foreground">{formatLocalDate(price.onDate)}</p>
      {price.status === 'rejected' ? (
        <div className="flex flex-wrap items-center justify-end gap-2">
          <Badge variant="outline" className="text-status-warning-text">{t('holding_prices.price_rejected')}</Badge>
          {writable ? (
            <ConfirmAction label={t('holding_prices.accept_price')} pending={accept.isPending}
              confirmation={t('holding_prices.accept_price_confirm', { price: formatUnits(price.value), previous: price.previous ? formatUnits(price.previous) : '' })}
              onConfirm={() => accept.mutate({ params: { path: { id: envelope, holdingId: holding.id } }, body: { onDate: price.onDate } })} />
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
