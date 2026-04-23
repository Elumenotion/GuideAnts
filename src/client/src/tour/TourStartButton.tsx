import { useTour } from './TourProvider';

interface TourStartButtonProps {
  screenId: string;
  inline?: boolean; // If true, renders as inline button for header
  className?: string; // Optional to allow custom styling/animation
}

export function TourStartButton({ screenId, inline = false, className }: TourStartButtonProps) {
  const { startTour, isRegistered } = useTour();
  
  if (!isRegistered(screenId)) return null;

  const handleClick = () => {
    startTour(screenId);
  };

  if (inline) {
    return (
      <button
        type="button"
        onClick={handleClick}
        className={`h-10 w-10 text-gray-700 bg-white border border-gray-300 rounded-md hover:bg-gray-50 transition-colors flex items-center justify-center ${className ?? ''}`}
        aria-label="Start tour"
        title="Help"
      >
        <svg xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24" strokeWidth={2} stroke="currentColor" className="w-4 h-4">
          <path strokeLinecap="round" strokeLinejoin="round" d="M9.879 7.519c1.171-1.025 3.071-1.025 4.242 0 1.172 1.025 1.172 2.687 0 3.712-.203.179-.43.326-.67.442-.745.361-1.45.999-1.45 1.827v.75M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0Zm-9 5.25h.008v.008H12v-.008Z" />
        </svg>
        <span className="sr-only">Help</span>
      </button>
    );
  }

  return (
    <button
      type="button"
      onClick={handleClick}
      className={`fixed bottom-4 right-4 z-50 rounded-full bg-gray-800 text-white w-9 h-9 shadow-md hover:bg-gray-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500 ${className ?? ''}`}
      aria-label="Start guided tour"
      title="Start guided tour"
    >
      ?
    </button>
  );
}
