export interface ElectronAPI {
  // IPC Communication
  on: (channel: string, callback: (...args: any[]) => void) => void;
  removeAllListeners: (channel: string) => void;
  
  // External links and navigation
  openExternal: (url: string) => Promise<void>;
  
  // Zoom controls
  getZoom?: () => number;
  setZoom?: (level: number) => void;
}

declare global {
  interface Window {
    electron?: ElectronAPI;
  }
}

// This ensures the file is treated as a module
export {} 