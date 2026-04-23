import { useEffect, useState } from 'react'

export const useAppPort = () => {
  const [appPort, setAppPort] = useState<number | null>(null)

  useEffect(() => {
    // Only run in Electron environment
    if (!window.electron) return

    const handlePortUpdate = (port: number) => {
      console.log('Received app port update:', port)
      setAppPort(port)
    }

    // Listen for port updates from main process
    window.electron.on('set-app-port', handlePortUpdate)

    return () => {
      window.electron?.removeAllListeners('set-app-port')
    }
  }, [])

  return appPort
} 