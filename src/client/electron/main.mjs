import { app, BrowserWindow, Menu, ipcMain, shell, dialog } from 'electron'
import express from 'express'
import http from 'http'
import path from 'path'
import { fileURLToPath } from 'url'
import contextMenu from 'electron-context-menu'
import { createServer } from 'net'
import updaterPkg from 'electron-updater'
import { readFileSync } from 'fs'

const { autoUpdater } = updaterPkg

const __filename = fileURLToPath(import.meta.url)
const __dirname = path.dirname(__filename)
const isDev = process.env.NODE_ENV === 'development'

// No external logger; use console so we don't add runtime deps
// autoUpdater.logger can be set if you later add a logger with info/warn/error API

// Dynamically resolve the icon so it works in both dev (loads from /public) and
// packaged builds (file placed next to the executable in the resources folder).
const iconPath = isDev
  ? path.join(__dirname, '..', 'public', 'maskable-icon.png')
  : path.join(process.resourcesPath, 'maskable-icon.png')

console.log('Starting Electron app...')
console.log('Environment:', process.env.NODE_ENV)
console.log('isDev:', isDev)
console.log('__dirname:', __dirname)

if (isDev) {
  app.commandLine.appendSwitch('remote-debugging-port', '9222')
}

// Function to find an available port
function findAvailablePort(startPort = 3000) {
  return new Promise((resolve, reject) => {
    const server = createServer()
    server.listen(startPort, () => {
      const { port } = server.address()
      server.close(() => resolve(port))
    })
    server.on('error', () => {
      findAvailablePort(startPort + 1).then(resolve, reject)
    })
  })
}

// Global variables
let selectedPort = null
let lastManualCheck = false

function readPublishUrlFromPackageJson() {
  try {
    const pkgPath = path.join(__dirname, '..', 'package.json')
    const raw = readFileSync(pkgPath, 'utf-8')
    const pkg = JSON.parse(raw)
    const url = pkg?.build?.publish?.[0]?.url
    return typeof url === 'string' && url.length > 0 ? url : undefined
  } catch (e) {
    return undefined
  }
}

function configureUpdaterFeedForDevIfNeeded() {
  if (!app.isPackaged) {
    const envUrl = process.env.UPDATE_URL
    const pkgUrl = envUrl || readPublishUrlFromPackageJson()
    if (pkgUrl && typeof autoUpdater.setFeedURL === 'function') {
      try {
        // Removed forceDevUpdateConfig to avoid requiring dev-app-update.yml
        autoUpdater.setFeedURL({ provider: 'generic', url: pkgUrl })
        console.info('[Updater] Dev feed configured:', pkgUrl)
      } catch (e) {
        console.error('[Updater] Failed to set dev feed URL', e)
      }
    } else {
      console.warn('[Updater] No dev feed URL available; set UPDATE_URL or build.publish.url')
    }
  }
}

function registerAutoUpdaterHandlers(mainWindow) {
  autoUpdater.on('checking-for-update', () => {
    console.info('AutoUpdater: checking for update')
    if (mainWindow && !mainWindow.isDestroyed()) {
      mainWindow.webContents.send('updater:status', 'Checking for updates...')
    }
  })

  autoUpdater.on('update-available', (info) => {
    console.info('AutoUpdater: update available', info.version)
    if (mainWindow && !mainWindow.isDestroyed()) {
      mainWindow.webContents.send('updater:status', `Update available: v${info.version}`)
      if (lastManualCheck) {
        dialog.showMessageBox(mainWindow, {
          type: 'info',
          title: 'Update available',
          message: `Version ${info.version} is available. Downloading in the background...`
        }).catch(() => {})
      }
    }
    lastManualCheck = false
  })

  autoUpdater.on('update-not-available', () => {
    console.info('AutoUpdater: update not available')
    if (mainWindow && !mainWindow.isDestroyed()) {
      mainWindow.webContents.send('updater:status', 'You are on the latest version.')
      if (lastManualCheck) {
        dialog.showMessageBox(mainWindow, {
          type: 'info',
          title: 'No updates',
          message: 'You are on the latest version.'
        }).catch(() => {})
      }
      // Clear any progress bar just in case
      try { mainWindow.setProgressBar(-1) } catch {}
    }
    lastManualCheck = false
  })

  autoUpdater.on('error', (err) => {
    console.error('AutoUpdater error:', err)
    if (mainWindow && !mainWindow.isDestroyed()) {
      mainWindow.webContents.send('updater:status', `Update error: ${err?.message || String(err)}`)
      if (lastManualCheck) {
        dialog.showMessageBox(mainWindow, {
          type: 'error',
          title: 'Update check failed',
          message: err?.message || String(err)
        }).catch(() => {})
      }
      try { mainWindow.setProgressBar(-1) } catch {}
    }
    lastManualCheck = false
  })

  autoUpdater.on('download-progress', (progressObj) => {
    const value = Math.max(0, Math.min(1, (progressObj?.percent || 0) / 100))
    console.info(`AutoUpdater: download ${Math.round(value * 100)}%`)
    try {
      if (mainWindow && !mainWindow.isDestroyed()) {
        mainWindow.setProgressBar(value)
      }
    } catch {}
  })

  autoUpdater.on('update-downloaded', async (info) => {
    console.info('AutoUpdater: update downloaded', info.version)
    try { if (mainWindow && !mainWindow.isDestroyed()) mainWindow.setProgressBar(-1) } catch {}
    if (mainWindow && !mainWindow.isDestroyed()) {
      const result = await dialog.showMessageBox(mainWindow, {
        type: 'question',
        buttons: ['Restart now', 'Later'],
        defaultId: 0,
        cancelId: 1,
        title: 'Update ready',
        message: `Version ${info.version} has been downloaded. Restart to install now?`
      })
      if (result.response === 0) {
        autoUpdater.quitAndInstall()
      }
    } else {
      autoUpdater.quitAndInstall()
    }
  })
}

function createWindow() {
  console.log('Creating window...')
  
  const mainWindow = new BrowserWindow({
    width: 1200,
    height: 800,
    // Ensure the window can be resized with the mouse on all platforms (especially Windows)
    resizable: true,
    thickFrame: true, // Windows-specific: guarantees OS resize borders are present
    show: false, // Don't show the window until it's ready
    icon: iconPath, // custom application icon
    webPreferences: {
      nodeIntegration: true,
      contextIsolation: true,
      preload: path.join(__dirname, 'preload.mjs'),
      enableWebSQL: false,
      spellcheck: true,
      backgroundThrottling: false,
      nativeWindowOpen: true
    }
  })

  // Ensure proper focus handling
  mainWindow.webContents.on('before-input-event', (event, input) => {
    // Log input events in development
    if (isDev) {
      console.log('Input event:', input);
    }
  });

  // Handle crashed and responsive events
  mainWindow.webContents.on('crashed', () => {
    console.error('Renderer process crashed');
    mainWindow.reload();
  });

  mainWindow.on('unresponsive', () => {
    console.error('Window became unresponsive');
    mainWindow.reload();
  });

  mainWindow.on('responsive', () => {
    console.log('Window became responsive again');
  });

  // Handle window focus events
  mainWindow.on('focus', () => {
    mainWindow.webContents.send('window-focus');
  });

  mainWindow.on('blur', () => {
    mainWindow.webContents.send('window-blur');
  });

  // Handle window restore events
  mainWindow.on('restore', () => {
    // Force a refocus when window is restored
    mainWindow.focus();
  });

  const startURL = isDev
    ? 'http://localhost:5173'
    : `http://localhost:${selectedPort}`

  console.log('Loading URL:', startURL)

  // Show window when it's ready
  mainWindow.once('ready-to-show', () => {
    console.log('Window ready to show')
    mainWindow.show()
    if (isDev) {
      mainWindow.webContents.openDevTools()
    }
  })

  mainWindow.loadURL(startURL)
    .then(() => {
      console.log('Window loaded successfully')
    })
    .catch((err) => {
      console.error('Failed to load window:', err)
    })

  mainWindow.webContents.on('did-fail-load', (event, errorCode, errorDescription) => {
    console.error('Failed to load:', errorCode, errorDescription)
    // Forward the error to the renderer process
    try {
      const webContents = event.sender;
      if (webContents && !webContents.isDestroyed() && typeof webContents.send === 'function') {
        webContents.send('did-fail-load', errorCode, errorDescription);
      }
    } catch (err) {
      console.error('Failed to send error to renderer:', err)
    }
  })

  // Handle external links
  mainWindow.webContents.setWindowOpenHandler(({ url }) => {
    console.log('Handling external URL:', url)

    // Allow MSAL to open its authentication popup (initially about:blank, then navigates to Azure)
    if (url === 'about:blank' || url.startsWith('https://login.microsoftonline.com')) {
      return {
        action: 'allow',
        overrideBrowserWindowOptions: {
          width: 450,
          height: 700,
          title: 'Sign in',
          resizable: false,
          autoHideMenuBar: true,
          parent: mainWindow,
          modal: true,
          webPreferences: {
            nodeIntegration: false,
            contextIsolation: true,
            sandbox: true,
            nativeWindowOpen: true
          }
        }
      }
    }
    
    // Check if this is an internal application route (hash route)
    // The renderer uses window.open with the current origin + hash path
    // e.g. http://localhost:PORT/#/projects/... or file://.../#/projects/...
    const isInternalRoute = url.includes('#/projects/') && (url.startsWith('http://localhost') || url.startsWith('file://'));
    
    if (isInternalRoute) {
      return {
        action: 'allow',
        overrideBrowserWindowOptions: {
          width: 1200,
          height: 800,
          autoHideMenuBar: true,
          webPreferences: {
            nodeIntegration: true,
            contextIsolation: true,
            preload: path.join(__dirname, 'preload.mjs'),
            enableWebSQL: false,
            spellcheck: true,
            backgroundThrottling: false,
            nativeWindowOpen: true
          }
        }
      }
    }

    // For any other external link, open in user's browser
    shell.openExternal(url)
    return { action: 'deny' }
  })

  // Create the custom menu
  const template = [
    {
      label: 'File',
      submenu: [
        {
          label: 'New Project',
          click: () => {
            try { if (!mainWindow.isDestroyed()) mainWindow.webContents.stop() } catch {}
            const destUrl = isDev
              ? 'http://localhost:5173/#/new-project'
              : `http://localhost:${selectedPort}/#/new-project`
            try { if (!mainWindow.isDestroyed()) mainWindow.loadURL(destUrl) } catch {}
          }
        },
        { type: 'separator' },
        {
          label: 'Sign Out',
          click: () => {
            try {
              if (!mainWindow.isDestroyed()) {
                // Ask renderer to perform MSAL logout so tokens are cleared
                mainWindow.webContents.send('sign-out')
              }
            } catch {}
          }
        },
        { type: 'separator' },
        { role: 'quit' }
      ]
    },
    { role: 'editMenu' },
    { role: 'viewMenu' },
    { role: 'windowMenu' },
    {
      label: 'Help',
      submenu: [
        {
          label: 'Check for Updates…',
          click: () => {
            try {
              console.info('Manual update check triggered')
              lastManualCheck = true
              const p = autoUpdater.checkForUpdates()
              if (p && typeof p.then === 'function') {
                p.then((result) => {
                  console.info('Manual update check completed', result?.updateInfo)
                }).catch((e) => {
                  console.error('Manual update check failed', e)
                })
              }
            } catch (e) {
              console.error('Failed to trigger manual update check', e)
              lastManualCheck = false
            }
          }
        },
        {
          label: 'Open Update Log',
          click: async () => {
            try {
              let logPath = ''
              try {
                const mod = await import('electron-log')
                const electronLog = mod?.default || mod
                logPath = electronLog?.transports?.file?.getFile?.().path || ''
              } catch {}
              if (!logPath) {
                const userData = app.getPath('userData')
                logPath = path.join(userData, 'logs', 'main.log')
              }
              if (logPath) {
                await shell.openPath(logPath)
              } else {
                await dialog.showMessageBox(mainWindow, {
                  type: 'info',
                  title: 'Update log',
                  message: 'Log path could not be determined.'
                })
              }
            } catch (e) {
              console.error('Failed to open update log', e)
            }
          }
        }
      ]
    }
  ]

  const menu = Menu.buildFromTemplate(template)
  Menu.setApplicationMenu(menu)

  return mainWindow
}

async function startStaticServer() {
  if (isDev) return; // dev server handled by Vite

  // Find an available port starting from 3000
  selectedPort = await findAvailablePort(3000)
  console.log(`Found available port: ${selectedPort}`)

  const appServer = express();

  // Path to React build output inside the asar/unpacked resources.
  const distPath = path.join(__dirname, '..', 'dist');
  appServer.use(express.static(distPath));

  // SPA fallback – always serve index.html for unknown routes
  appServer.get('*', (_req, res) => {
    res.sendFile(path.join(distPath, 'index.html'));
  });

  const server = http.createServer(appServer);
  server.listen(selectedPort, '127.0.0.1', () => console.log(`Static server started on http://localhost:${selectedPort}`));
  server.unref();
}

// Handle app ready
app.whenReady()
  .then(async () => {
    // Enable native-like context menu with copy/paste for all BrowserWindows
    contextMenu({
      showSearchWithGoogle: false,
      showInspectElement: isDev,
    });
    console.log('App is ready')

    // In production, optionally initialize electron-log for updater logs
    if (app.isPackaged) {
      try {
        const mod = await import('electron-log')
        const electronLog = mod?.default || mod
        if (electronLog) {
          autoUpdater.logger = electronLog
          try { electronLog.transports.file.level = 'info' } catch {}
          try { console.log('Updater log file:', electronLog.transports.file.getFile().path) } catch {}
        }
      } catch {}
    }

    // Ensure downloads happen automatically and install on quit
    autoUpdater.autoDownload = true
    autoUpdater.autoInstallOnAppQuit = true

    // Configure updater feed in dev so manual checks work without packaging
    configureUpdaterFeedForDevIfNeeded()

    await startStaticServer();
    const mainWindow = createWindow()

    // Auto-update: register handlers and check on startup in production
    registerAutoUpdaterHandlers(mainWindow)
    if (!isDev) {
      try {
        await autoUpdater.checkForUpdatesAndNotify()
      } catch (e) {
        console.error('Initial auto update check failed', e)
      }
    }
    
    // Send the selected port to the renderer process
    if (!isDev && selectedPort) {
      mainWindow.webContents.on('did-finish-load', () => {
        mainWindow.webContents.send('set-app-port', selectedPort)
      })
    }
    
    // Handle opening external links
    ipcMain.handle('open-external', async (_, url) => {
      if (url) {
        await shell.openExternal(url);
      }
    });

    // All authentication is now handled entirely within the renderer process
    // using msal-browser.  No IPC handlers are required here.
  })
  .catch((err) => {
    console.error('Failed to initialize app:', err)
  })

app.on('window-all-closed', () => {
  console.log('All windows closed')
  if (process.platform !== 'darwin') {
    app.quit()
  }
})

app.on('activate', () => {
  console.log('App activated')
  if (BrowserWindow.getAllWindows().length === 0) {
    createWindow()
  }
})

// Handle any uncaught errors
process.on('uncaughtException', (error) => {
  console.error('Uncaught exception:', error)
})

// Log any unhandled rejections
process.on('unhandledRejection', (reason, promise) => {
  console.error('Unhandled Rejection at:', promise, 'reason:', reason)
})