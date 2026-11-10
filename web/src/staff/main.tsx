import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import '../shared/fonts.css';
import '../shared/ui.css';
import '../shared/layout.css';
import './admin.css';
import './theme.css';
import { StaffApp } from './StaffApp';

createRoot(document.getElementById('root')!).render(<StrictMode><StaffApp /></StrictMode>);
