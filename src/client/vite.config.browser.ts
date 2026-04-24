import { defineConfig, loadEnv, createLogger } from 'vite'
import react from '@vitejs/plugin-react'
import { visualizer } from 'rollup-plugin-visualizer'
import { resolve } from 'path'

// Create custom logger to filter out misleading warnings
const customLogger = createLogger()
const originalWarn = customLogger.warn
customLogger.warn = (msg, options) => {
  // Suppress misleading warning about /public/ in route paths
  if (msg.includes('Files in the public directory are served at the root path')) {
    return
  }
  originalWarn(msg, options)
}

export default defineConfig(({ mode }) => {
  // Load env file based on `mode` in the current working directory.
  const env = loadEnv(mode, process.cwd(), '')
  const configuredApiUrl = env.VITE_API_URL?.trim()
  if (!configuredApiUrl) {
    throw new Error(`VITE_API_URL is required for browser mode "${mode}".`)
  }

  let proxyTarget: string | undefined
  if (/^https?:\/\//i.test(configuredApiUrl)) {
    try {
      proxyTarget = new URL(configuredApiUrl).origin
    } catch {
      throw new Error(`VITE_API_URL must be a valid absolute URL or a same-origin path like "/api", got "${configuredApiUrl}".`)
    }
  } else if (!configuredApiUrl.startsWith('/')) {
    throw new Error(`VITE_API_URL must be an absolute URL or a root-relative path like "/api", got "${configuredApiUrl}".`)
  }

  const analyze = process.env.ANALYZE === 'true'
  const serverConfig = proxyTarget
    ? {
        port: 5173,
        host: true, // Allow external connections for browser testing
        proxy: {
          '/api': {
            target: proxyTarget,
            changeOrigin: true,
          },
          '/chat': {
            target: proxyTarget,
            changeOrigin: true,
          },
          '/sandbox': {
            target: proxyTarget,
            changeOrigin: true,
          },
        },
      }
    : {
        port: 5173,
        host: true,
      }
  
  return {
    plugins: [
      react(),
      analyze && visualizer({
        open: true,
        filename: 'dist-browser/stats.html',
        gzipSize: true,
        brotliSize: true,
      }),
    ].filter(Boolean),
    customLogger,
    base: '/', // Use absolute paths for browser deployment
    build: {
      outDir: 'dist-browser',
      emptyOutDir: true,
      rollupOptions: {
        output: {
          manualChunks(id) {
            // Keep React in main bundle - it's needed by everything
            // Only split out libraries that are NOT tightly coupled with React
            
            // Authentication (standalone, lazy-loadable)
            if (id.includes('node_modules/@azure/')) {
              return 'azure-auth';
            }
            // Lexical rich text editor
            if (id.includes('node_modules/lexical') || id.includes('node_modules/@lexical/')) {
              return 'lexical';
            }
            // Markdown processing (mostly standalone parsing)
            if (id.includes('node_modules/remark-') ||
                id.includes('node_modules/rehype-') ||
                id.includes('node_modules/unified') ||
                id.includes('node_modules/mdast') ||
                id.includes('node_modules/hast') ||
                id.includes('node_modules/micromark')) {
              return 'markdown';
            }
            // Driver.js touring (standalone)
            if (id.includes('node_modules/driver.js')) {
              return 'driver';
            }
          }
        }
      }
    },
    resolve: {
      alias: {
        '@': resolve(__dirname, 'src'),
      },
    },
    // Define global env variables for browser build
    define: {
      'process.env.NODE_ENV': JSON.stringify(process.env.NODE_ENV),
      'process.env.BUILD_TARGET': JSON.stringify('browser'),
      // Remove Electron-specific env vars for browser build
      'process.env.ELECTRON_ENABLE_LOGGING': JSON.stringify('false')
    },
    server: serverConfig,
    preview: {
      port: 4173,
      host: true,
    },
  }
})
