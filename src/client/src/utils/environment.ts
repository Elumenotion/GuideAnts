/**
 * Utility functions for detecting the runtime environment
 */

/**
 * Check if the app is running in Electron
 * Uses multiple detection methods for reliability
 */
export const isElectron = (): boolean => {
  // Method 1: Check for our custom electron API
  if (typeof window !== 'undefined' && window.electron !== undefined) {
    return true;
  }
  
  // Method 2: Check for electron in user agent
  if (typeof navigator !== 'undefined' && navigator.userAgent?.toLowerCase().includes('electron')) {
    return true;
  }
  
  // Method 3: Check for electron in process (if available)
  if (typeof process !== 'undefined' && process.versions?.electron) {
    return true;
  }
  
  // Method 4: Check for file:// protocol (common in Electron)
  if (typeof window !== 'undefined' && window.location?.protocol === 'file:') {
    return true;
  }
  
  return false;
};

/**
 * Check if the app is running in a browser
 */
export const isBrowser = (): boolean => {
  return !isElectron();
};

/**
 * Check if the app is running on iOS (any browser)
 * All iOS browsers use WebKit under the hood due to Apple's requirements,
 * so they all have the same behavior regarding memory management, storage, etc.
 * This detects: Safari, Chrome (CriOS), Firefox (FxiOS), Edge, and any other iOS browser.
 */
export const isIOS = (): boolean => {
  if (typeof navigator === 'undefined' || typeof window === 'undefined') return false;
  
  const ua = navigator.userAgent;
  
  // Standard iOS detection (iPhone, iPad, iPod)
  const isIOSDevice = /iPad|iPhone|iPod/.test(ua) && !(window as any).MSStream;
  
  // iPadOS 13+ reports as MacIntel but has touch support
  const isIPadOS13Plus = navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1;
  
  return isIOSDevice || isIPadOS13Plus;
};

/**
 * Get the build target from environment variables
 */
export const getBuildTarget = (): 'electron' | 'browser' => {
  return (process.env.BUILD_TARGET as 'electron' | 'browser') || 'electron';
};

/**
 * Check if running in development mode
 */
export const isDevelopment = (): boolean => {
  return process.env.NODE_ENV === 'development' || import.meta.env.DEV;
};

/**
 * Check if running in production mode
 */
export const isProduction = (): boolean => {
  return process.env.NODE_ENV === 'production' || import.meta.env.PROD;
};

/**
 * Get the appropriate router component based on RUNTIME environment detection
 * HashRouter for Electron (file:// protocol compatibility)
 * BrowserRouter for web browsers (clean URLs)
 */
export const getRouterType = (): 'hash' | 'browser' => {
  // RUNTIME DETECTION ONLY: Check if actually running in Electron
  if (isElectron()) {
    return 'hash';
  }
  // If not in Electron, always use browser router (even if built for Electron)
  return 'browser';
};

/**
 * Handle external link opening based on environment
 */
export const openExternalLink = (url: string): void => {
  if (isElectron() && window.electron?.openExternal) {
    window.electron.openExternal(url);
  } else {
    // Browser fallback
    window.open(url, '_blank', 'noopener,noreferrer');
  }
};

/**
 * Debug function to log environment detection results
 * Useful for troubleshooting routing issues
 */
export const debugEnvironment = (): void => {
  console.group('🔍 Environment Detection Debug');
  console.log('isElectron():', isElectron());
  console.log('isBrowser():', isBrowser());
  console.log('getRouterType():', getRouterType());
  console.log('getBuildTarget():', getBuildTarget());
  console.log('isDevelopment():', isDevelopment());
  console.log('isProduction():', isProduction());
  console.log('window.electron exists:', typeof window !== 'undefined' && !!window.electron);
  console.log('navigator.userAgent:', typeof navigator !== 'undefined' ? navigator.userAgent : 'N/A');
  console.log('window.location.protocol:', typeof window !== 'undefined' ? window.location?.protocol : 'N/A');
  console.log('process.versions?.electron:', typeof process !== 'undefined' ? process.versions?.electron : 'N/A');
  console.groupEnd();
};
