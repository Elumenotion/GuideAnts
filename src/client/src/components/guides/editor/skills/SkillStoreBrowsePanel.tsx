import { useCallback, useEffect, useState } from 'react';
import { FaSearch, FaSpinner, FaStore } from 'react-icons/fa';
import { api } from '../../../../services/api';
import type { AssistantSkillDto, SkillCatalogEntryDto } from '../../../../types/guides';
import { isLocalSkillConflict } from './skillStoreHelpers';

interface SkillStoreBrowsePanelProps {
  existingSkills: AssistantSkillDto[];
  /** Stages the chosen snapshot as a pending skill upload. */
  onInstall: (entry: SkillCatalogEntryDto, overwrite: boolean) => Promise<void>;
}

export function SkillStoreBrowsePanel({ existingSkills, onInstall }: SkillStoreBrowsePanelProps) {
  const [search, setSearch] = useState('');
  const [entries, setEntries] = useState<SkillCatalogEntryDto[]>([]);
  const [loading, setLoading] = useState(false);
  const [busyName, setBusyName] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async (term: string) => {
    setLoading(true);
    setError(null);
    try {
      setEntries(await api.skillStore.list(term || undefined));
    } catch (loadError) {
      setError(loadError instanceof Error ? loadError.message : 'Failed to load the skill store.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load('');
  }, [load]);

  const install = async (entry: SkillCatalogEntryDto, overwrite: boolean) => {
    setBusyName(entry.name);
    setError(null);
    try {
      await onInstall(entry, overwrite);
    } catch (installError) {
      setError(installError instanceof Error ? installError.message : 'Install failed.');
    } finally {
      setBusyName(null);
    }
  };

  return (
    <div className="border-t border-gray-200 pt-4">
      <div className="flex items-center gap-2">
        <FaStore className="h-4 w-4 text-gray-500" />
        <h3 className="text-sm font-semibold text-gray-900">From the skill store</h3>
      </div>

      <div className="mt-3 flex gap-2">
        <input
          type="search"
          value={search}
          placeholder="Search skills"
          onChange={(event) => setSearch(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === 'Enter') {
              event.preventDefault();
              void load(search);
            }
          }}
          className="flex-1 rounded-md border border-gray-300 px-3 py-2 text-sm"
          aria-label="Search the skill store"
        />
        <button
          type="button"
          onClick={() => void load(search)}
          disabled={loading}
          className="inline-flex items-center gap-2 rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-700 hover:bg-gray-50 disabled:opacity-50"
        >
          {loading ? <FaSpinner className="h-3 w-3 animate-spin" /> : <FaSearch className="h-3 w-3" />}
          Search
        </button>
      </div>

      {error && (
        <div className="mt-3 rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700" role="alert">
          {error}
        </div>
      )}

      {!loading && entries.length === 0 && !error && (
        <p className="mt-3 text-sm text-gray-500">No published skills match this search.</p>
      )}

      <ul className="mt-3 space-y-2">
        {entries.map((entry) => {
          const conflict = isLocalSkillConflict(entry, existingSkills);
          const busy = busyName === entry.name;
          return (
            <li
              key={entry.id}
              className="flex items-start justify-between gap-3 rounded-md border border-gray-200 px-3 py-2"
            >
              <div className="min-w-0">
                <p className="truncate text-sm font-medium text-gray-900">
                  {entry.name}
                  <span className="ml-2 rounded bg-gray-100 px-1.5 py-0.5 text-xs text-gray-600">
                    v{entry.currentVersion}
                  </span>
                </p>
                <p className="mt-0.5 line-clamp-2 text-xs text-gray-600">{entry.description}</p>
              </div>
              <button
                type="button"
                disabled={busy}
                onClick={() => void install(entry, conflict)}
                className={`shrink-0 rounded-md px-3 py-1.5 text-sm font-medium disabled:opacity-50 ${
                  conflict
                    ? 'border border-amber-300 bg-amber-50 text-amber-900 hover:bg-amber-100'
                    : 'bg-blue-600 text-white hover:bg-blue-700'
                }`}
              >
                {busy ? 'Installing…' : conflict ? 'Overwrite' : 'Install'}
              </button>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
