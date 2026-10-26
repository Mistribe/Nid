import type { NotificationView } from './api';

export function notification(n: number, over: Partial<NotificationView> = {}): NotificationView {
  return {
    id: `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`, kind: 'test', level: 'info', title: `Titre ${n}`, body: `Corps ${n}`,
    link: '/routine', itemIds: [], arrivedAt: '2026-10-20T08:00:00Z', read: false, state: 'open', critical: false, ...over,
  };
}
