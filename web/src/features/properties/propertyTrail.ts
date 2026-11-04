import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import type { Crumb } from '@/components/page/trail';
import { PROPERTY, type PropertyDetail } from './api';

/** A property sub-page's trail before its view loads: the property named from the cache if already read (no request), else « Bien ». */
export function usePropertyTrail(id: string): Crumb[] {
  const { t } = useTranslation();
  const { $api } = useApi();
  const name = useQueryClient().getQueryData<PropertyDetail>($api.queryOptions('get', PROPERTY, { params: { path: { id } } }).queryKey)?.name;
  return [{ label: t('nav.properties'), to: '/properties' }, { label: name ?? t('properties.item_fallback'), to: '/properties/$id', params: { id } }];
}
