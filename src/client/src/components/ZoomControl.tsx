import { useEffect } from 'react';

/**
 * Global component to handle zoom behavior across the entire application using
 * Electron's native webFrame zoom. This avoids layout bugs caused by CSS scale/zoom.
 */
const ZoomControl: React.FC = () => {
    useEffect(() => {
        const MIN = 0.33;
        const MAX = 5;
        const STEP = 0.33;

        // Helpers provided by preload script
        const getZoom = (): number => {
            // @ts-ignore – exposed by preload
            return window.electron?.getZoom?.() ?? 1;
        };
        const setZoom = (z: number) => {
            // Clamp in renderer to avoid invalid factors
            const bounded = Math.min(Math.max(z, MIN), MAX);
            // @ts-ignore – exposed by preload
            window.electron?.setZoom?.(bounded);
        };

        const wheel = (e: WheelEvent) => {
            if (e.ctrlKey || e.metaKey) {
                e.preventDefault();
                const delta = e.deltaY > 0 ? -STEP : STEP;
                setZoom(getZoom() + delta);
            }
        };

        const key = (e: KeyboardEvent) => {
            // Ignore typing inside editable fields
            if (e.target && (e.target as HTMLElement).isContentEditable) return;
            if (e.target instanceof HTMLInputElement ||
                e.target instanceof HTMLTextAreaElement ||
                e.target instanceof HTMLSelectElement) return;

            if (e.ctrlKey || e.metaKey) {
                if (['+', '=', '-'].includes(e.key)) {
                    e.preventDefault();
                    const delta = e.key === '-' ? -STEP : STEP;
                    setZoom(getZoom() + delta);
                } else if (e.key === '0') {
                    e.preventDefault();
                    setZoom(1);
                }
            }
        };

        window.addEventListener('wheel', wheel, { passive: false });
        window.addEventListener('keydown', key);

        // Ensure we start at 100 %
        setZoom(1);

        return () => {
            window.removeEventListener('wheel', wheel);
            window.removeEventListener('keydown', key);
        };
    }, []);

    return null;
};

export default ZoomControl; 