import { Link } from 'react-router-dom';

interface StartCardProps {
  to: string;
  title: string;
  description: string;
  icon?: React.ReactNode;
  disabled?: boolean;
  tourId?: string;
}

const StartCard = ({ 
  to, 
  title, 
  description, 
  icon, 
  disabled = false, 
  tourId
}: StartCardProps) => {
  const content = (
    <div className={`
      relative block rounded-lg border p-4 shadow-sm transition-all
      ${disabled 
        ? 'border-gray-200 bg-gray-50 cursor-not-allowed' 
        : 'border-gray-200 bg-white hover:shadow-md hover:border-gray-300'
      }
    `} {...(tourId ? { ['data-tour-id']: tourId } : {})}>
      <div className="flex items-start">
        {icon && (
          <div className={`mr-3 text-xl ${disabled ? 'text-gray-400' : 'text-blue-600'}`} aria-hidden>
            {icon}
          </div>
        )}
        <div className="flex-1">
          <div className={`text-sm font-medium flex items-center ${disabled ? 'text-gray-400' : 'text-gray-900'}`}>
            {title}
            {!disabled && (
              <span className="ml-2 text-gray-400 group-hover:translate-x-0.5 transition-transform">→</span>
            )}
          </div>
          <div className={`mt-1 text-xs ${disabled ? 'text-gray-400' : 'text-gray-600'}`}>
            {description}
          </div>
        </div>
      </div>
    </div>
  );

  if (disabled) {
    return content;
  }

  return (
    <Link to={to} className="group">
      {content}
    </Link>
  );
};

export default StartCard;

