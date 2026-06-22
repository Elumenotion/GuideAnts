import { Link } from 'react-router-dom';

export interface HeaderIconLinkButtonProps {
  to: string;
  title: string;
  icon: React.ReactNode;
  tourId?: string;
}

export function HeaderIconLinkButton({ to, title, icon, tourId }: HeaderIconLinkButtonProps) {
  return (
    <Link
      to={to}
      title={title}
      aria-label={title}
      className="flex h-10 w-10 items-center justify-center rounded-md border border-gray-300 bg-white text-gray-700 transition-colors hover:bg-gray-50"
      {...(tourId ? { ['data-tour-id']: tourId } : {})}
    >
      {icon}
      <span className="sr-only">{title}</span>
    </Link>
  );
}
