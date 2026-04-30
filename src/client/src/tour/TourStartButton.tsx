import { useTour } from './TourProvider';

interface TourStartButtonProps {
  screenId: string;
  inline?: boolean;
  className?: string;
}

export function TourStartButton({ screenId, inline = false, className }: TourStartButtonProps) {
  const { startTour, isRegistered } = useTour();

  const hasTour = isRegistered(screenId);

  const handleClick = () => {
    if (hasTour) {
      startTour(screenId);
    }
  };

  const icon = (
    <svg xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24" strokeWidth={2} stroke="currentColor" className="w-4 h-4">
      <path strokeLinecap="round" strokeLinejoin="round" d="M9.879 7.519c1.171-1.025 3.071-1.025 4.242 0 1.172 1.025 1.172 2.687 0 3.712-.203.179-.43.326-.67.442-.745.361-1.45.999-1.45 1.827v.75M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0Zm-9 5.25h.008v.008H12v-.008Z" />
    </svg>
  );

  if (inline) {
    return (
      <button
        type="button"
        onClick={handleClick}
        disabled={!hasTour}
        className={`h-10 w-10 border rounded-md transition-colors flex items-center justify-center ${
          hasTour
            ? 'text-gray-700 bg-white border-gray-300 hover:bg-gray-50'
            : 'text-gray-400 bg-gray-50 border-gray-200 cursor-default'
        } ${className ?? ''}`}
        aria-label={hasTour ? 'Start tour' : 'Tour coming soon'}
        title={hasTour ? 'Help' : 'Tour coming soon'}
      >
        {icon}
        <span className="sr-only">{hasTour ? 'Help' : 'Tour coming soon'}</span>
      </button>
    );
  }

  return (
    <button
      type="button"
      onClick={handleClick}
      disabled={!hasTour}
      className={`fixed bottom-4 right-4 z-50 rounded-full w-9 h-9 shadow-md focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500 ${
        hasTour
          ? 'bg-gray-800 text-white hover:bg-gray-700'
          : 'bg-gray-400 text-gray-200 cursor-default'
      } ${className ?? ''}`}
      aria-label={hasTour ? 'Start guided tour' : 'Tour coming soon'}
      title={hasTour ? 'Start guided tour' : 'Tour coming soon'}
    >
      ?
    </button>
  );
}
