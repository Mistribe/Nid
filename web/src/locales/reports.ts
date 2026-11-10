// Loaded with the annual review, so not with the initial bundle (frontend/architecture.md §11); the dashboard's link to it
// keeps its label in fr.json (`dashboard.year_review`).
import i18n from '@/app/i18n';
import strings from './fr.reports.json';

i18n.addResourceBundle('fr', 'translation', strings, true, true);
