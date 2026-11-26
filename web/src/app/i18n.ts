import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import fr from '@/locales/fr.json';
import { elision } from '@/lib/elision';

// French only, every string by key from the start (frontend/architecture.md §11).
void i18n.use(initReactI18next).init({
  lng: 'fr',
  fallbackLng: 'fr',
  resources: { fr: { translation: fr } },
  interpolation: { escapeValue: false }, // React escapes already
});
// #729: « {{name, de}} » writes « de Bob » or « d’Alice », the French elision before a vowel.
i18n.services.formatter?.add('de', (value: unknown) => `${elision(String(value)) ? 'd’' : 'de '}${String(value)}`);

export default i18n;
