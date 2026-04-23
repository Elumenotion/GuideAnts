import { useEffect } from 'react';
import { autoCorrectCurrentUrl } from '../../utils/urlSanitizer';

/**
 * Component that automatically corrects malformed URLs on mount
 * Should be rendered early in the app lifecycle
 */
export function UrlCorrector() {
    useEffect(() => {
        // Auto-correct URL on component mount
        const wasCorrected = autoCorrectCurrentUrl();
        
        if (wasCorrected) {
            console.log('URL was corrected and page will reload');
            return; // Page will reload with corrected URL
        }
        
        // Set up a listener for future URL changes to catch malformed URLs
        const handleHashChange = () => {
            // Small delay to let the URL change complete
            setTimeout(() => {
                autoCorrectCurrentUrl();
            }, 10);
        };
        
        // Listen for hash changes (for hash router)
        window.addEventListener('hashchange', handleHashChange);
        
        // Listen for popstate (for browser router)
        window.addEventListener('popstate', handleHashChange);
        
        return () => {
            window.removeEventListener('hashchange', handleHashChange);
            window.removeEventListener('popstate', handleHashChange);
        };
    }, []);
    
    // This component doesn't render anything
    return null;
}
