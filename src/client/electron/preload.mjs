import { contextBridge, ipcRenderer, webFrame } from 'electron';

console.log('Preload script starting...');

try {
  // Expose protected methods that allow the renderer process to use
  // the ipcRenderer without exposing the entire object
  contextBridge.exposeInMainWorld(
    'electron',
    {
      on: (channel, callback) => {
        // Whitelist channels
        const validChannels = ['sign-out', 'navigate', 'did-fail-load', 'window-focus', 'window-blur', 'set-app-port'];
        if (validChannels.includes(channel)) {
          ipcRenderer.on(channel, (event, ...args) => callback(...args));
        }
      },
      removeAllListeners: (channel) => {
        const validChannels = ['sign-out', 'navigate', 'did-fail-load', 'window-focus', 'window-blur', 'set-app-port'];
        if (validChannels.includes(channel)) {
          ipcRenderer.removeAllListeners(channel);
        }
      },
      openExternal: (url) => ipcRenderer.invoke('open-external', url),
      // Zoom helpers for renderer (React) code
      getZoom: () => webFrame.getZoomFactor(),
      setZoom: (factor) => {
        try {
          webFrame.setZoomFactor(factor);
        } catch (e) {
          console.error('Failed to set zoom factor', e);
        }
      }
    }
  );

  // Authentication-specific IPC helpers removed – the renderer process now
  // performs MSAL authentication directly without round-trips to the main
  // process.

  console.log('Preload script completed successfully');
} catch (error) {
  console.error('Error in preload script:', error);
} 