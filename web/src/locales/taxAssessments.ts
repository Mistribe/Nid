// The tax notice's strings, loaded by its screens only: the dashboard pulls the tax bundle into the initial one (frontend/architecture.md §11).
import i18n from '@/app/i18n';
import strings from './fr.tax-assessments.json';

i18n.addResourceBundle('fr', 'translation', strings, true, true);
