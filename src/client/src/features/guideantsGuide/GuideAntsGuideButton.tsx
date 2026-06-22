import { FiMessageCircle } from 'react-icons/fi';
import { useAuth } from '../../contexts/AuthContext';
import { useGuideAntsGuide } from './GuideAntsGuideProvider';

export function GuideAntsGuideButton() {
  const { status, role } = useAuth();
  const { isOpen, toggle } = useGuideAntsGuide();

  if (status !== 'authenticated' || role === 'Pending') {
    return null;
  }

  return (
    <button
      type="button"
      onClick={() => {
        void toggle();
      }}
      aria-label="GuideAnts Guide"
      aria-pressed={isOpen}
      title="GuideAnts Guide"
      className={`h-10 w-10 border rounded-md transition-colors flex items-center justify-center ${
        isOpen ? 'border-blue-400 bg-blue-50 text-blue-700' : 'border-gray-300 hover:bg-gray-50 text-gray-700 bg-white'
      }`}
      data-tour-id="guideants-guide.button"
      data-guideants-guide-trigger="true"
    >
      <FiMessageCircle className="h-4 w-4" />
      <span className="sr-only">GuideAnts Guide</span>
    </button>
  );
}
