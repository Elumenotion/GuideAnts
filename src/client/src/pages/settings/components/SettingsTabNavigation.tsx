import type { IconType } from 'react-icons';
import { FaDatabase, FaHome, FaMicrochip, FaSlidersH, FaThLarge } from 'react-icons/fa';
import { SettingsTab } from '../types';

interface SettingsTabNavigationProps {
  activeTab: SettingsTab;
  onTabChange: (tab: SettingsTab) => void;
}

const tabs: Array<{ key: SettingsTab; label: string; icon: IconType }> = [
  { key: 'overview', label: 'Overview', icon: FaHome },
  { key: 'connections', label: 'Connections', icon: FaDatabase },
  { key: 'models-runtime', label: 'Models & Runtime', icon: FaMicrochip },
  { key: 'services', label: 'Services', icon: FaThLarge },
  { key: 'infrastructure', label: 'Infrastructure', icon: FaSlidersH },
];

export function SettingsTabNavigation({ activeTab, onTabChange }: SettingsTabNavigationProps) {
  return (
    <div className="border-b border-gray-200 bg-white px-8">
      <div className="mx-auto max-w-7xl">
        <nav className="flex gap-8" aria-label="Settings tabs">
          {tabs.map((tab) => {
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
