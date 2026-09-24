import type fr from '@/locales/fr.json';

declare module 'i18next' {
  interface CustomTypeOptions {
    resources: { translation: typeof fr };
  }
}
