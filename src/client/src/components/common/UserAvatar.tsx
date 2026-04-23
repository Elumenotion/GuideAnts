import { useMemo } from 'react';

interface UserAvatarProps {
  userId?: string;
  userName?: string;
  userEmail?: string;
  /**
   * Size variant. `xs` is meant for stacked avatar layouts (50 % of normal size).
   */
  size?: 'xs' | 'sm' | 'md' | 'lg';
  className?: string;
}

/**
 * UserAvatar displays user initials in a colored circle.
 * Falls back to "U" if no user information is available.
 */
export default function UserAvatar({ 
  userId, 
  userName, 
  userEmail, 
  size = 'md', 
  className = '' 
}: UserAvatarProps) {
  const initials = useMemo(() => {
    if (userName) {
      // Extract initials from name (e.g., "John Doe" -> "JD")
      const nameParts = userName.trim().split(/\s+/);
      if (nameParts.length >= 2) {
        return `${nameParts[0][0]}${nameParts[1][0]}`.toUpperCase();
      }
      return nameParts[0][0].toUpperCase();
    }
    
    if (userEmail) {
      // Extract initials from email (e.g., "john.doe@example.com" -> "JD")
      const emailPart = userEmail.split('@')[0];
      const parts = emailPart.split(/[._-]/);
      if (parts.length >= 2) {
        return `${parts[0][0]}${parts[1][0]}`.toUpperCase();
      }
      return parts[0][0].toUpperCase();
    }
    
    return 'U'; // Default fallback
  }, [userName, userEmail]);

  // Generate consistent color based on userId or fallback to user info
  const backgroundColor = useMemo(() => {
    const seed = userId || userName || userEmail || 'default';
    // Simple hash function to generate consistent colors
    let hash = 0;
    for (let i = 0; i < seed.length; i++) {
      hash = seed.charCodeAt(i) + ((hash << 5) - hash);
    }
    
    // Generate a pastel color based on hash
    if (seed === 'default') {
      return '#E5E7EB'; // Tailwind gray-200
    }
    const hue = Math.abs(hash) % 360;
    return `hsl(${hue}, 65%, 75%)`;
  }, [userId, userName, userEmail]);

  const textColor = useMemo(() => {
    // Use a darker shade for text
    const seed = userId || userName || userEmail || 'default';
    let hash = 0;
    for (let i = 0; i < seed.length; i++) {
      hash = seed.charCodeAt(i) + ((hash << 5) - hash);
    }
    if (seed === 'default') {
      return '#374151'; // Tailwind gray-700
    }
    const hue = Math.abs(hash) % 360;
    return `hsl(${hue}, 65%, 35%)`;
  }, [userId, userName, userEmail]);

  const sizeClasses: Record<NonNullable<UserAvatarProps['size']>, string> = {
    // Extra-small variant used in stacked assistant-editor avatar layout
    xs: 'h-5 w-5 sm:h-6 sm:w-6 lg:h-7 lg:w-7 text-[10px] sm:text-[11px] lg:text-xs',
    sm: 'h-8 w-8 text-xs',
    md: 'h-10 w-10 sm:h-12 sm:w-12 lg:h-14 lg:w-14 text-sm sm:text-base lg:text-lg',
    lg: 'h-12 w-12 text-base'
  };

  return (
    <div 
      className={`rounded-full flex items-center justify-center font-medium ${sizeClasses[size]} ${className}`}
      style={{ backgroundColor, color: textColor }}
      title={userName || userEmail || 'User'}
    >
      {initials}
    </div>
  );
} 