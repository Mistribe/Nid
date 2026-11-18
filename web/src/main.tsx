import './styles/globals.css';
import './app/i18n';
import { bootstrap } from './app/bootstrap';
import { reloadOnMissingChunk } from './app/newBuild';

window.addEventListener('vite:preloadError', (event) =>
  reloadOnMissingChunk(event, import.meta.env.VITE_NID_VERSION ?? 'dev', () => sessionStorage, window.location));

void bootstrap({ root: document.getElementById('root')!, location: window.location });
