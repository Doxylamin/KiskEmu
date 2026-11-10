import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import '../shared/fonts.css';
import '../shared/ui.css';
import '../shared/layout.css';
import './player.css';
import { PlayerApp } from './PlayerApp';

// The player site uses the light theme of the shared tokens (shared/ui.css).
document.documentElement.dataset.theme = 'light';
createRoot(document.getElementById('root')!).render(<StrictMode><PlayerApp /></StrictMode>);
