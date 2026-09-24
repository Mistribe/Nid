import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import fr from '@/locales/fr.json';

// French only, every string by key from the start (frontend/architecture.md §11).
void i18n.use(initReactI18next).init({
  lng: 'fr',
  fallbackLng: 'fr',
  resources: { fr: { translation: fr } },
  interpolation: { escapeValue: false }, // React escapes already
});

export default i18n;
