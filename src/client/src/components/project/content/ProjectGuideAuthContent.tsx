import { useEffect, useMemo, useState } from 'react';
import { useLocation } from 'react-router';
import { NotebookAuthProviderDto, NotebookTemplateDto } from '../../../types/project';
import { api } from '../../../services/api';
import { beginOAuthConnection } from '../../../utils/notebookAuth';
import { useToast } from '../../common/Toast';

interface ProjectGuideAuthContentProps {
    projectId: string; // reserved for future persistence calls
    templateId: string;
    canEdit?: boolean;
}

type ProviderState = {
    provider: NotebookAuthProviderDto;
    // OAuth fields
    clientId?: string;
    tenant?: string;
    // Service HTTP fields
    headerName?: string;
    headerValue?: string;
    // Connection state
    isConnected?: boolean;
    // Validation state
    clientIdError?: string;
    tenantError?: string;
    headerValueError?: string;
    isDirty?: boolean;
};

export function ProjectGuideAuthContent({ projectId, templateId, canEdit = false }: ProjectGuideAuthContentProps) {
    const location = useLocation();
    const { showToast } = useToast();
    const [template, setTemplate] = useState<NotebookTemplateDto | null>(null);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState<string | null>(null);
    const [providers, setProviders] = useState<ProviderState[]>([]);
    const [saving, setSaving] = useState(false);

    useEffect(() => {
        let cancelled = false;
        const load = async () => {
            setLoading(true); setError(null);
            try {
                const [t, existingOverrides] = await Promise.all([
                    api.projects.notebookTemplates.getById(templateId, projectId),
                    api.projects.externalAuth.list(projectId).catch(() => [])
                ]);
                if (cancelled) return;
                setTemplate(t);
                
                const initial = (t.authProviders || [])
                    .filter(p => {
                        const policy = (p.userConfigPolicy as unknown as string | undefined)?.toString().toLowerCase();
                        return policy === 'optional' || policy === 'required';
                    })
                    .map(async p => {
                        // Check for existing override
                        const override = existingOverrides.find(o => o.providerId === p.id);

                        let isConnected = false;
                        if (p.authType.toString().toLowerCase() === 'oauth') {
                            try {
                                const status = await api.projects.externalAuth.oauth.status(projectId, p.id);
                                isConnected = status.connected;
                            } catch {
                                isConnected = false;
                            }
                        }

                        return {
                            provider: p,
                            clientId: override?.clientId || p.clientId || '',
                            tenant: override?.tenant || p.tenant || '',
                            headerName: override?.headerName || p.headerName,
                            headerValue: override?.hasHeaderValue ? '••••••••' : '', // Mask existing values
                            isConnected,
                            isDirty: false
                        };
                    });
                setProviders(await Promise.all(initial));
            } catch (e) {
                setError('Failed to load guide authorization details.');
            } finally {
                if (!cancelled) setLoading(false);
            }
        };
        load();
        return () => { cancelled = true; };
    }, [templateId, projectId]);

    // Handle OAuth success from callback
    useEffect(() => {
        const urlParams = new URLSearchParams(location.search);
        const oauthSuccess = urlParams.get('oauthSuccess');
        
        if (oauthSuccess) {
            showToast({
                type: 'success',
                title: 'OAuth Connection Successful',
                message: `Successfully connected to ${oauthSuccess}`
            });

            api.projects.externalAuth.oauth.status(projectId, oauthSuccess)
                .then(status => {
                    setProviders(prev => {
                        const next = [...prev];
                        const providerIndex = next.findIndex(p => p.provider.id === oauthSuccess);
                        if (providerIndex >= 0) {
                            next[providerIndex] = { ...next[providerIndex], isConnected: status.connected };
                        }
                        return next;
                    });
                })
                .finally(() => {
                    const newUrl = new URL(window.location.href);
                    newUrl.searchParams.delete('oauthSuccess');
                    window.history.replaceState({}, '', newUrl.toString());
                });
        }
    }, [location.search, projectId, showToast]); // Remove providers dependency to prevent loops

    const handleSaveKeys = async () => {
        setSaving(true);
        try {
            await Promise.all(serviceProviders
                .filter(sp => sp.isDirty && !sp.headerValueError)
                .map(sp => api.projects.externalAuth.save(projectId, sp.provider.id, {
                    authType: 'service_http',
                    headerName: sp.headerName || sp.provider.headerName || 'Authorization',
                    headerValue: sp.headerValue || ''
                }))
            );
            // Mark saved providers as clean
            const next = [...providers];
            serviceProviders.forEach(sp => {
                if (sp.isDirty && !sp.headerValueError) {
                    const idx = providers.indexOf(sp);
                    next[idx] = { ...sp, isDirty: false };
                }
            });
            setProviders(next);
        } finally {
            setSaving(false);
        }
    };

    // Validation functions
    const validateClientId = (value: string): string | undefined => {
        if (!value.trim()) return 'Client ID is required';
        // Basic GUID format check
        const guidRegex = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
        if (!guidRegex.test(value.trim())) return 'Client ID must be a valid GUID format';
        return undefined;
    };

    const validateTenant = (value: string): string | undefined => {
        if (!value.trim()) return 'Tenant is required';
        const trimmed = value.trim();
        // Allow common values or GUID format
        if (trimmed === 'organizations' || trimmed === 'common') return undefined;
        const guidRegex = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
        if (guidRegex.test(trimmed)) return undefined;
        return 'Tenant must be "organizations", "common", or a valid tenant GUID';
    };

    const validateHeaderValue = (value: string, isRequired: boolean): string | undefined => {
        if (isRequired && !value.trim()) return 'API key/token is required';
        if (value.trim() && value.trim().length < 8) return 'API key/token appears too short';
        return undefined;
    };

    // Update provider validation state
    const updateProviderValidation = (index: number, updates: Partial<ProviderState>) => {
        const next = [...providers];
        const current = next[index];
        const updated = { ...current, ...updates, isDirty: true };
        
        // Run validation
        if (updated.provider.authType.toString().toLowerCase() === 'oauth') {
            updated.clientIdError = validateClientId(updated.clientId || '');
            updated.tenantError = validateTenant(updated.tenant || '');
        } else if (updated.provider.authType.toString().toLowerCase().includes('service')) {
            const isRequired = updated.provider.userConfigPolicy.toString().toLowerCase() === 'required';
            updated.headerValueError = validateHeaderValue(updated.headerValue || '', isRequired);
        }
        
        next[index] = updated;
        setProviders(next);
    };

    const serviceProviders = useMemo(() => providers.filter(p => {
        const authType = (p.provider.authType as unknown as string).toString().toLowerCase();
        return authType === 'service_http' || authType === 'servicehttp';
    }), [providers]);
    const oauthProviders = useMemo(() => providers.filter(p => {
        const authType = (p.provider.authType as unknown as string).toString().toLowerCase();
        return authType === 'oauth';
    }), [providers]);


    const handleSaveOAuthOverride = async (index: number) => {
        setSaving(true);
        try {
            const ps = oauthProviders[index];
            if (!ps || ps.clientIdError || ps.tenantError) return;
            
            await api.projects.externalAuth.save(projectId, ps.provider.id, {
                authType: 'oauth',
                clientId: ps.clientId?.trim(),
                tenant: ps.tenant?.trim()
            });
            
            // Mark as clean
            const next = [...providers];
            const providerIdx = providers.indexOf(ps);
            next[providerIdx] = { ...ps, isDirty: false };
            setProviders(next);
        } finally {
            setSaving(false);
        }
    };

    const handleTestOAuth = async (index: number) => {
        const ps = oauthProviders[index];
        if (!ps || ps.clientIdError || ps.tenantError) return;

        setSaving(true);
        
        try {
            const currentUrl = window.location.pathname + window.location.search;
            const separator = currentUrl.includes('?') ? '&' : '?';
            const returnUrl = currentUrl + `${separator}section=guideAuthorization&item=${templateId}`;

            await beginOAuthConnection(projectId, {
                ...ps.provider,
                clientId: ps.clientId?.trim() || ps.provider.clientId,
                tenant: ps.tenant?.trim() || ps.provider.tenant
            }, returnUrl);
            
        } catch (error) {
            console.error('OAuth test failed:', error);
            setError('Failed to start OAuth test');
            setSaving(false);
        }
    };

    if (loading) return <div className="p-4">Loading guide authorization…</div>;
    if (error) return <div className="p-4 text-red-600">{error}</div>;
    if (!template) return null;

    return (
        <div className="space-y-6">
            <div className="flex items-center justify-between">
                <div>
                    <h2 className="text-xl font-bold">Guide Authorization</h2>
                    <p className="text-gray-600">Configure external access required by {template.templateName}.</p>
                </div>
            </div>

            {oauthProviders.length > 0 && (
                <div className="border rounded p-4">
                    <h3 className="font-medium mb-2">OAuth Connections</h3>
                    <p className="text-sm text-gray-600 mb-3">Override client configuration and/or connect your account.</p>
                    <div className="space-y-4">
                        {oauthProviders.map((ps) => {
                            const idx = providers.indexOf(ps);
                            return (
                                <div key={ps.provider.id} className="border rounded p-4">
                                    <div className="space-y-4">
                                        <div className="flex items-start justify-between">
                                            <div>
                                                <div className="font-medium">{ps.provider.id}</div>
                                                <div className="text-xs text-gray-500">Scopes: {(ps.provider.scopes || []).join(', ')}</div>
                                            </div>
                                            <div className="flex items-center gap-2">
                                                {ps.isConnected && (
                                                    <span className="text-xs text-green-600 font-medium">✓ Connected</span>
                                                )}
                                                <button
                                                    onClick={() => handleTestOAuth(idx)}
                                                    disabled={!canEdit || saving || !!ps.clientIdError || !!ps.tenantError}
                                                    className="px-3 py-1 text-sm bg-green-600 text-white rounded disabled:opacity-50"
                                                >
                                                    {ps.isConnected ? 'Test Again' : 'Test OAuth'}
                                                </button>
                                            </div>
                                        </div>
                                        
                                        <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
                                            <div>
                                                <label className="block text-sm text-gray-600 mb-1">Client ID</label>
                                                <input
                                                    type="text"
                                                    value={ps.clientId || ''}
                                                    onChange={(e) => updateProviderValidation(idx, { clientId: e.target.value })}
                                                    disabled={!canEdit}
                                                    className={`w-full px-3 py-2 border rounded focus:outline-none focus:ring-2 disabled:opacity-50 ${
                                                        ps.clientIdError ? 'border-red-500 focus:ring-red-500' : 'border-gray-300 focus:ring-blue-500'
                                                    }`}
                                                    placeholder={ps.provider.clientId || 'Enter client ID'}
                                                />
                                                {ps.clientIdError && (
                                                    <div className="text-red-500 text-xs mt-1">{ps.clientIdError}</div>
                                                )}
                                            </div>
                                            <div>
                                                <label className="block text-sm text-gray-600 mb-1">Tenant</label>
                                                <input
                                                    type="text"
                                                    value={ps.tenant || ''}
                                                    onChange={(e) => updateProviderValidation(idx, { tenant: e.target.value })}
                                                    disabled={!canEdit}
                                                    className={`w-full px-3 py-2 border rounded focus:outline-none focus:ring-2 disabled:opacity-50 ${
                                                        ps.tenantError ? 'border-red-500 focus:ring-red-500' : 'border-gray-300 focus:ring-blue-500'
                                                    }`}
                                                    placeholder={ps.provider.tenant || 'organizations/common/<tenantId>'}
                                                />
                                                {ps.tenantError && (
                                                    <div className="text-red-500 text-xs mt-1">{ps.tenantError}</div>
                                                )}
                                            </div>
                                        </div>
                                        
                                        <div className="flex items-center gap-2">
                                            <button
                                                onClick={() => handleSaveOAuthOverride(idx)}
                                                disabled={!canEdit || saving || !ps.isDirty || !!ps.clientIdError || !!ps.tenantError}
                                                className="px-4 py-2 text-sm bg-blue-600 text-white rounded disabled:opacity-50"
                                            >
                                                {saving ? 'Saving…' : 'Save'}
                                            </button>
                                            <button
                                                onClick={() => {
                                                    const next = [...providers];
                                                    next[idx] = {
                                                        ...ps,
                                                        clientId: ps.provider.clientId || '',
                                                        tenant: ps.provider.tenant || '',
                                                        clientIdError: undefined,
                                                        tenantError: undefined,
                                                        isDirty: false
                                                    };
                                                    setProviders(next);
                                                }}
                                                disabled={!canEdit || !ps.isDirty}
                                                className="px-4 py-2 text-sm border rounded disabled:opacity-50"
                                            >
                                                Reset
                                            </button>
                                        </div>
                                    </div>
                                </div>
                            );
                        })}
                    </div>
                </div>
            )}

            {serviceProviders.length > 0 && (
                <div className="border rounded p-4">
                    <h3 className="font-medium mb-2">Service API Keys</h3>
                    <div className="space-y-4">
                        {serviceProviders.map((ps) => (
                            <div key={ps.provider.id} className="space-y-2">
                                <div className="font-medium">{ps.provider.id}</div>
                                <label className="block text-sm text-gray-600">
                                    {ps.provider.headerName || 'Authorization'}
                                </label>
                                <input
                                    type="text"
                                    value={ps.headerValue || ''}
                                    onChange={(e) => updateProviderValidation(providers.indexOf(ps), { headerValue: e.target.value })}
                                    disabled={!canEdit}
                                    className={`w-full px-3 py-2 border rounded focus:outline-none focus:ring-2 disabled:opacity-50 ${
                                        ps.headerValueError ? 'border-red-500 focus:ring-red-500' : 'border-gray-300 focus:ring-blue-500'
                                    }`}
                                    placeholder={ps.provider.valueTemplate || ''}
                                />
                                {ps.headerValueError && (
                                    <div className="text-red-500 text-xs mt-1">{ps.headerValueError}</div>
                                )}
                            </div>
                        ))}
                        <div>
                            <button
                                onClick={handleSaveKeys}
                                                disabled={!canEdit || saving || !serviceProviders.some(p => !!p.isDirty && !p.headerValueError)}
                                className="px-4 py-2 text-sm bg-blue-600 text-white rounded hover:bg-blue-700 disabled:opacity-50"
                            >
                                {saving ? 'Saving…' : 'Save Keys'}
                            </button>
                        </div>
                    </div>
                </div>
            )}

            {oauthProviders.length === 0 && serviceProviders.length === 0 && (
                <div className="border rounded p-4">
                    <p className="text-gray-600">This guide has no user-configurable providers. If it requires access, it will use the template defaults.</p>
                </div>
            )}
        </div>
    );
}

export default ProjectGuideAuthContent;


