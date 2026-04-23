import { useLocation, useNavigate } from 'react-router-dom';
import { FiSettings } from 'react-icons/fi';

interface SettingsButtonProps {
  className?: string;
}

export function SettingsButton({ className = '' }: SettingsButtonProps) {
  const navigate = useNavigate();
  const location = useLocation();
  const isCurrentPage = location.pathname === '/settings';

  return (
    <button
      onClick={() => {
        if (!isCurrentPage) {
          navigate('/settings');
        }
      }}
      aria-label="Open Settings"
      title="Settings"
      disabled={isCurrentPage}
      className={`h-10 w-10 border rounded-md transition-colors flex items-center justify-center ${
        isCurrentPage ? 'bg-gray-100 text-gray-400 border-gray-200 cursor-default' : 'hover:bg-gray-50'
      } ${className}`}
    >
      <FiSettings className="h-4 w-4" />
      <span className="sr-only">Settings</span>
    </button>
  );
}
