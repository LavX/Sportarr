import { NavLink } from 'react-router-dom';

const destinations = [
  { label: 'Providers', path: '/iptv/settings/providers' },
  { label: 'Recording', path: '/iptv/settings/recording' },
  { label: 'Advanced', path: '/iptv/settings/advanced' },
];

export default function IptvSettingsNav() {
  return (
    <nav aria-label="IPTV options" className="mb-6 overflow-x-auto border-b border-gray-800">
      <div className="flex min-w-max gap-1">
        {destinations.map((destination) => (
          <NavLink
            key={destination.path}
            to={destination.path}
            className={({ isActive }) => `-mb-px min-h-11 border-b-2 px-4 py-3 text-sm font-medium transition-colors ${
              isActive
                ? 'border-red-500 text-white'
                : 'border-transparent text-gray-400 hover:text-gray-200'
            }`}
          >
            {destination.label}
          </NavLink>
        ))}
      </div>
    </nav>
  );
}
