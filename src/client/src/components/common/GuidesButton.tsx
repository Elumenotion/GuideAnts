import { useNavigate } from 'react-router';
import { FiZap } from 'react-icons/fi';
import { useAuth } from '../../contexts/AuthContext';

interface GuidesButtonProps {
  className?: string;
}

/**
 * Guides nav button — opens the global (all-projects) guide list at /guides,
 * whose cards carry no publishing UI. The routes behind it are admin-only
 * (RequireAdmin), so the button renders only for admins.
 */
export function GuidesButton({ className = '' }: GuidesButtonProps) {
  const navigate = useNavigate();
  const { status, role } = useAuth();

  if (status !== 'authenticated' || role !== 'Admin') {
    return null;
  }

  return (
    <button
      onClick={() => navigate('/guides')}
      aria-label="Open Guides"
      title="Guides"
      className={`h-10 w-10 border rounded-md transition-colors flex items-center justify-center hover:bg-gray-50 ${className}`}
    >
      <FiZap className="h-4 w-4" />
      <span className="sr-only">Guides</span>
    </button>
  );
}
