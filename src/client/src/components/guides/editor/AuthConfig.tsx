import { useState } from 'react';
import { FaTrash, FaChevronDown, FaPlus } from 'react-icons/fa';
import { AuthProviderDto } from '../../../types/guides';

interface AuthConfigProps {
  providers: AuthProviderDto[];
  onChange: (providers: AuthProviderDto[]) => void;
}

export function AuthConfig({ providers, onChange }: AuthConfigProps) {
  const [expandedIndex, setExpandedIndex] = useState<number | null>(null);

  const handleAddProvider = () => {
    const newProvider: AuthProviderDto = {
      providerId: '',
      authType: 'oauth',
      clientId: '',
      scopes: [],
    };
    onChange([...providers, newProvider]);
    setExpandedIndex(providers.length);
  };

  const handleRemoveProvider = (index: number) => {
    onChange(providers.filter((_, i) => i !== index));
    if (expandedIndex === index) setExpandedIndex(null);
  };

  const handleUpdateProvider = (index: number, updates: Partial<AuthProviderDto>) => {
    const newProviders = [...providers];
    newProviders[index] = { ...newProviders[index], ...updates };
    onChange(newProviders);
  };

  const handleScopeChange = (index: number, scopesText: string) => {
    const scopes = scopesText.split(',').map((s) => s.trim()).filter((s) => s);
    handleUpdateProvider(index, { scopes });
  };

  return (
    <div className="space-y-4">
      <h2 className="text-lg font-semibold text-gray-900">Authentication Configuration</h2>
      <p className="text-sm text-gray-600">
        Configure authentication providers for accessing external APIs or services.
      </p>

      <div className="space-y-2">
        {providers.map((provider, index) => (
          <div key={index} className="border border-gray-300 rounded-md overflow-hidden">
            {/* Accordion Header */}
            <button
              type="button"
              onClick={() => setExpandedIndex(expandedIndex === index ? null : index)}
              className="w-full flex items-center justify-between p-4 bg-gray-50 hover:bg-gray-100 text-left"
            >
              <div className="flex items-center gap-2">
                <span className="text-sm font-medium text-gray-900">
                  {provider.providerId || 'New Provider'}
                </span>
                <span className="text-xs text-gray-500 bg-gray-200 px-2 py-0.5 rounded">
                  {provider.authType}
                </span>
              </div>
              <div className="flex items-center gap-2">
                <button
                  type="button"
                  onClick={(e) => {
                    e.stopPropagation();
                    handleRemoveProvider(index);
                  }}
                  className="text-red-600 hover:text-red-700 p-1"
                  title="Remove"
                >
                  <FaTrash className="w-4 h-4" />
                </button>
                <FaChevronDown
                  className={`w-4 h-4 text-gray-400 transition-transform ${
                    expandedIndex === index ? 'transform rotate-180' : ''
                  }`}
                />
              </div>
            </button>

            {/* Accordion Content */}
            {expandedIndex === index && (
              <div className="p-4 space-y-4 bg-white">
                {/* Provider ID */}
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Provider ID</label>
                  <input
                    type="text"
                    value={provider.providerId}
                    onChange={(e) => handleUpdateProvider(index, { providerId: e.target.value })}
                    placeholder="e.g., microsoft-graph"
                    className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                  />
                </div>

                {/* Auth Type */}
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Auth Type</label>
                  <select
                    value={provider.authType}
                    onChange={(e) => handleUpdateProvider(index, { authType: e.target.value })}
                    className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                  >
                    <option value="oauth">OAuth</option>
                    <option value="service_http">Service HTTP</option>
                  </select>
                </div>

                {/* OAuth-specific fields */}
                {provider.authType === 'oauth' && (
                  <>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-1">Client ID</label>
                      <input
                        type="text"
                        value={provider.clientId || ''}
                        onChange={(e) => handleUpdateProvider(index, { clientId: e.target.value })}
                        placeholder="OAuth client ID"
                        className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                      />
                    </div>

                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-1">Tenant (Optional)</label>
                      <input
                        type="text"
                        value={provider.tenant || ''}
                        onChange={(e) => handleUpdateProvider(index, { tenant: e.target.value })}
                        placeholder="Azure AD tenant ID"
                        className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                      />
                    </div>

                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-1">Scopes</label>
                      <input
                        type="text"
                        value={(provider.scopes || []).join(', ')}
                        onChange={(e) => handleScopeChange(index, e.target.value)}
                        placeholder="user.read, mail.send (comma-separated)"
                        className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                      />
                    </div>
                  </>
                )}

                {/* Service HTTP-specific fields */}
                {provider.authType === 'service_http' && (
                  <>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-1">Header Name</label>
                      <input
                        type="text"
                        value={provider.headerName || ''}
                        onChange={(e) => handleUpdateProvider(index, { headerName: e.target.value })}
                        placeholder="e.g., Authorization"
                        className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                      />
                    </div>

                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-1">Value Template</label>
                      <input
                        type="text"
                        value={provider.valueTemplate || ''}
                        onChange={(e) => handleUpdateProvider(index, { valueTemplate: e.target.value })}
                        placeholder="e.g., Bearer {token}"
                        className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                      />
                    </div>
                  </>
                )}

                {/* User Config Policy */}
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">
                    User Config Policy (Optional)
                  </label>
                  <input
                    type="text"
                    value={provider.userConfigPolicy || ''}
                    onChange={(e) => handleUpdateProvider(index, { userConfigPolicy: e.target.value })}
                    placeholder="Policy for user-specific configuration"
                    className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                  />
                </div>
              </div>
            )}
          </div>
        ))}
      </div>

      <button
        type="button"
        onClick={handleAddProvider}
        className="flex items-center gap-2 px-4 py-2 text-sm text-blue-600 border border-blue-300 rounded-md hover:bg-blue-50"
      >
        <FaPlus className="w-4 h-4" />
        Add Provider
      </button>
    </div>
  );
}


