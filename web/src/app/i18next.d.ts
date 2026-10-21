import type fr from '@/locales/fr.json';
import type board from '@/locales/fr.subscriptions-board.json';

declare module 'i18next' {
  interface CustomTypeOptions {
    resources: { translation: typeof fr; 'subscriptions-board': typeof board };
  }
}
