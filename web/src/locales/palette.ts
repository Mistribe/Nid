// Loaded with the ⌘K palette, so not with the initial bundle (frontend/architecture.md §11); the top bar's button keeps its
// label and shortcut in fr.json.
import i18n from '@/app/i18n';
import strings from './fr.palette.json';

i18n.addResourceBundle('fr', 'translation', strings, true, true);
