import './styles/globals.css';
import './app/i18n';
import { bootstrap } from './app/bootstrap';

void bootstrap({ root: document.getElementById('root')!, location: window.location });
