import type { IconType } from 'react-icons';
import { FaDatabase, FaHome, FaMicrochip, FaSatelliteDish, FaSlidersH, FaThLarge, FaUserCog, FaUsers } from 'react-icons/fa';
import { SettingsTab } from '../types';
import type { AppRole } from '../../../types/user';

interface SettingsTabNavigationProps {
  activeTab: SettingsTab;
  role: AppRole | null;
  onTabChange: (tab: SettingsTab) => void;
}

const tabs: Array<{ key: SettingsTab; label: string; icon: IconType; adminOnly?: boolean }> = [
  { key: 'overview', label: 'Overview', icon: FaHome, adminOnly: true },
  { key: 'personalization', label: 'Personalization', icon: FaUserCog },
  { key: 'users', label: 'Users', icon: FaUsers, adminOnly: true },
  { key: 'connections', label: 'Connections', icon: FaDatabase, adminOnly: true },
  { key: 'models-runtime', label: 'Models & Runtime', icon: FaMicrochip, adminOnly: true },
  { key: 'services', label: 'Services', icon: FaThLarge, adminOnly: true },
  { key: 'infrastructure', label: 'Infrastructure', icon: FaSlidersH, adminOnly: true },
  { key: 'telemetry', label: 'Telemetry', icon: FaSatelliteDish, adminOnly: true },
];

export function SettingsTabNavigation({ activeTab, role, onTabChange }: SettingsTabNavigationProps) {
  const visibleTabs = role === 'Admin'
    ? tabs
    : tabs.filter((tab) => !tab.adminOnly);

  return (
    <div className="border-b border-gray-200 bg-white px-8">
      <div className="mx-auto max-w-7xl">
        <nav className="flex gap-6 overflow-x-auto" aria-label="Settings tabs">
          {visibleTabs.map((tab) => {
            const Icon = tab.icon;
            const isActive = activeTab === tab.key;
            return (
              <button
                key={tab.key}
                type="button"
                onClick={() => onTabChange(tab.key)}
                className={`flex items-center gap-2 border-b-2 px-1 py-3 text-sm font-medium ${
                  isActive
                    ? 'border-blue-500 text-blue-600'
                    : 'border-transparent text-gray-500 hover:border-gray-300 hover:text-gray-700'
                }`}
                aria-current={isActive ? 'page' : undefined}
              >
                <Icon className="h-4 w-4" aria-hidden="true" />
                <span>{tab.label}</span>
              </button>
            );
          })}
        </nav>
      </div>
    </div>
  );
}
