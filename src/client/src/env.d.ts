/// <reference types="vite/client" />
/// <reference types="react" />

interface ImportMetaEnv {
  readonly VITE_API_URL: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}

// Electron API types
interface ElectronAPI {
  on: (channel: string, callback: (...args: any[]) => void) => void;
  removeAllListeners: (channel: string) => void;
  openExternal: (url: string) => Promise<void>;
  getZoom: () => number;
  setZoom: (factor: number) => void;
}

declare global {
  interface Window {
    electron: ElectronAPI;
  }
} 
