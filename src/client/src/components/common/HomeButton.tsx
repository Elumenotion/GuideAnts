import { useNavigate } from 'react-router-dom';

interface HomeButtonProps {
  className?: string;
}

export function HomeButton({ className = '' }: HomeButtonProps) {
  const navigate = useNavigate();

  return (
    <button
      onClick={() => navigate('/')}
      aria-label="Back to Home"
      title="Home"
      className={`h-10 w-10 border rounded-md hover:bg-gray-50 transition-colors flex items-center justify-center ${className}`}
    >
      <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="currentColor" className="w-4 h-4">
        <path d="M11.47 3.84a.75.75 0 0 1 1.06 0l8 8a.75.75 0 1 1-1.06 1.06l-.72-.72V19.5A2.25 2.25 0 0 1 16.5 21.75h-2.25a.75.75 0 0 1-.75-.75v-3.75a1.5 1.5 0 0 0-1.5-1.5h-1.5a1.5 1.5 0 0 0-1.5 1.5V21a.75.75 0 0 1-.75.75H4.5A2.25 2.25 0 0 1 2.25 19.5v-7.32l-.72.72a.75.75 0 1 1-1.06-1.06l8-8Z" />
      </svg>
      <span className="sr-only">Home</span>
    </button>
  );
}


