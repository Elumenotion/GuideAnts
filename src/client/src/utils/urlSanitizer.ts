import { getRouterType } from './environment';

/**
 * Sanitizes and corrects malformed URLs, especially hash routing issues
 */
export function sanitizeUrl(url: string): string {
    try {
        const urlObj = new URL(url);
        
        // Fix double hash issues (## -> #)
        if (urlObj.hash.startsWith('##')) {
            urlObj.hash = urlObj.hash.substring(1);
        }
        
        // For hash router, ensure proper hash structure
        if (getRouterType() === 'hash') {
            // If we have a hash but it doesn't start with #/, fix it
            if (urlObj.hash && !urlObj.hash.startsWith('#/')) {
                // Extract the route part (everything after #)
                const hashContent = urlObj.hash.substring(1);
                // If it looks like a route path, ensure it starts with /
                if (hashContent && !hashContent.startsWith('/')) {
                    urlObj.hash = `#/${hashContent}`;
                } else if (hashContent.startsWith('/')) {
                    urlObj.hash = `#${hashContent}`;
                }
            }
            
            // If no hash but we're in hash router mode and have a meaningful pathname
            if (!urlObj.hash && urlObj.pathname !== '/') {
                urlObj.hash = `#${urlObj.pathname}${urlObj.search}`;
                urlObj.pathname = '/';
                urlObj.search = '';
            }
        }
        
        return urlObj.toString();
    } catch (error) {
        console.warn('URL sanitization failed, returning original:', url, error);
        return url;
    }
}

/**
 * Extracts the route path from current URL, handling both hash and browser routing
 */
export function getCurrentRoutePath(): string {
    const routerType = getRouterType();
    
    if (routerType === 'hash') {
        const hash = window.location.hash;
        if (hash.startsWith('#/')) {
            return hash.substring(1); // Remove # but keep /
        } else if (hash.startsWith('#')) {
            const hashContent = hash.substring(1);
            return hashContent.startsWith('/') ? hashContent : `/${hashContent}`;
        }
        return '/';
    } else {
        return window.location.pathname + window.location.search;
    }
}

/**
 * Safely navigates to a URL with proper sanitization
 */
export function safeNavigate(url: string, replace: boolean = false): void {
    const sanitizedUrl = sanitizeUrl(url);
    
    if (replace) {
        window.location.replace(sanitizedUrl);
    } else {
        window.location.href = sanitizedUrl;
    }
}

/**
 * Constructs a proper URL for the current router type
 */
export function buildUrl(path: string, params?: URLSearchParams): string {
    const routerType = getRouterType();
    const origin = window.location.origin;
    
    // Ensure path starts with /
    const cleanPath = path.startsWith('/') ? path : `/${path}`;
    
    // Add query parameters if provided
    const queryString = params ? `?${params.toString()}` : '';
    const fullPath = `${cleanPath}${queryString}`;
    
    if (routerType === 'hash') {
        return `${origin}/#${fullPath}`;
    } else {
        return `${origin}${fullPath}`;
    }
}

/**
 * Auto-corrects the current URL if it's malformed
 */
export function autoCorrectCurrentUrl(): boolean {
    const currentUrl = window.location.href;
    const correctedUrl = sanitizeUrl(currentUrl);
    
    if (currentUrl !== correctedUrl) {
        console.log('Auto-correcting malformed URL:', currentUrl, '->', correctedUrl);
        window.location.replace(correctedUrl);
        return true;
    }
    
    return false;
}
