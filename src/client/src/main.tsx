//import React from 'react'
import ReactDOM from 'react-dom/client'
import App from './App'
import './index.css'
import { TourProvider } from './tour/TourProvider'
// Import electron types to ensure they're loaded
import type {} from './types/electron'
// Note: guideants web component is lazy-loaded in PublicGuide.tsx

ReactDOM.createRoot(document.getElementById('root')!).render(
  <TourProvider>
    <App />
  </TourProvider>
)