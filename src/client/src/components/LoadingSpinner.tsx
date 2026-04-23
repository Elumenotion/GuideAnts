import React from 'react';

interface LoadingSpinnerProps {
  message?: string;
}

const LoadingSpinner: React.FC<LoadingSpinnerProps> = ({ message = 'Loading your content...' }) => {
  return (
    <div className="flex flex-col items-center justify-center w-full min-h-[200px] p-4">
      <img 
        src="/guide.png" 
        alt="Loading..." 
        className="w-16 h-16 animate-bounce"
      />
      <p className="mt-4 text-gray-600 text-sm font-medium">{message}</p>
    </div>
  );
};

export default LoadingSpinner; 