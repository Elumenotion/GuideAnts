import { useEffect, useRef, useState } from 'react';
import { FaSignOutAlt, FaUserCircle } from 'react-icons/fa';
import { useNavigate } from 'react-router';
import { useAuth } from '../../contexts/AuthContext';
import { textButtonClassName } from '../../pages/settings/components/shared/ActionButtons';

export function HeaderUserMenu() {
  const navigate = useNavigate();
  const { user, logout } = useAuth();
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const handlePointerDown = (event: MouseEvent) => {
      if (!rootRef.current) {
        return;
      }
      if (!rootRef.current.contains(event.target as Node)) {
        setOpen(false);
      }
    };

    document.addEventListener('mousedown', handlePointerDown);
    return () => {
      document.removeEventListener('mousedown', handlePointerDown);
    };
  }, []);

  if (!user) {
    return null;
  }

  return (
    <div className="relative" ref={rootRef}>
      <button
        type="button"
        onClick={() => setOpen((previous) => !previous)}
        aria-label="User menu"
        title={`${user.name} (${user.role})`}
        className="h-10 w-10 border rounded-md transition-colors flex items-center justify-center hover:bg-gray-50 text-gray-700 border-gray-300 bg-white"
      >
        <FaUserCircle className="h-4 w-4" />
      </button>

      {open ? (
        <div className="absolute right-0 z-50 mt-2 w-64 rounded border border-gray-200 bg-white p-3 shadow-lg">
          <div className="mb-3">
            <div className="truncate text-sm font-semibold text-gray-900">{user.name}</div>
            <div className="truncate text-xs text-gray-600">{user.email}</div>
            <div className="mt-1 text-xs text-gray-600">Role: {user.role}</div>
          </div>
          <button
            type="button"
            className={`${textButtonClassName('danger')} w-full`}
            onClick={() => {
              logout();
              navigate('/login', { replace: true });
            }}
          >
            <span className="text-[12px] leading-none" aria-hidden="true">
              <FaSignOutAlt />
            </span>
            <span>Sign Out</span>
          </button>
        </div>
      ) : null}
    </div>
  );
}
