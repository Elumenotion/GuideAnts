import { useRef, useState } from 'react';
import LexicalEditor from '../../../notebook/conversations/LexicalEditor';
import type { LexicalEditorRef } from '../../../notebook/conversations/LexicalEditor';
import { useToast } from '../../../common/Toast';
import { buildAuthoredSkillSave, type SkillImportResult } from './skillImportHelpers';
import {
  buildSkillDescriptionImportWarnings,
  getSkillDescriptionWarning,
  normalizeSkillDescription,
  SKILL_DESCRIPTION_LIMITS_HINT,
  SKILL_DESCRIPTION_MAX_LENGTH,
  SKILL_DESCRIPTION_RECOMMENDED_LENGTH,
} from './skillDescriptionLimits';

interface AuthorSkillEditorProps {
  isOpen: boolean;
  nextDisplayOrder: number;
  onClose: () => void;
  onAuthored: (result: SkillImportResult) => void;
}

export function AuthorSkillEditor({ isOpen, nextDisplayOrder, onClose, onAuthored }: AuthorSkillEditorProps) {
  const { showToast } = useToast();
  const editorRef = useRef<LexicalEditorRef>(null);
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  if (!isOpen) {
    return null;
  }

  const descriptionLength = description.length;
  const descriptionWarning = getSkillDescriptionWarning(descriptionLength);

  const handleSave = async () => {
    setError(null);
    if (!name.trim()) {
      setError('Skill name is required.');
      return;
    }

    if (!description.trim()) {
      setError('Skill description is required.');
      return;
    }

    const normalizedDescription = normalizeSkillDescription(description);
    const descriptionWarnings = buildSkillDescriptionImportWarnings(normalizedDescription);

    setSaving(true);
    try {
      const body = editorRef.current?.getValue() ?? '';
      const skill = await buildAuthoredSkillSave({
        name: name.trim(),
        description: normalizedDescription.description,
        body,
        enabled: true,
        displayOrder: nextDisplayOrder,
      });
      if (descriptionWarnings.length > 0) {
        showToast({
          type: 'warning',
          title: `Saved "${skill.name}" with description adjustments`,
          message: descriptionWarnings.join(' '),
        });
      }
      onAuthored({
        skill,
        originalMarkdown: '',
        descriptionWarnings,
      });
      setName('');
      setDescription('');
      editorRef.current?.setValue('');
      onClose();
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : 'Failed to author skill.');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div
      className="fixed inset-0 z-50 flex items-end justify-center bg-black/40 p-4 sm:items-center"
      role="dialog"
      aria-modal="true"
      aria-labelledby="author-skill-title"
      onClick={onClose}
    >
      <div
        className="max-h-[90vh] w-full max-w-3xl overflow-auto rounded-lg bg-white p-6 shadow-xl"
        onClick={(event) => event.stopPropagation()}
      >
        <h2 id="author-skill-title" className="text-lg font-semibold text-gray-900">
          Author skill
        </h2>

        <div className="mt-4 space-y-4">
          <div>
            <label className="block text-sm font-medium text-gray-700" htmlFor="skill-name">
              Name
            </label>
            <input
              id="skill-name"
              value={name}
              onChange={(event) => setName(event.target.value)}
              className="mt-1 w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-gray-700" htmlFor="skill-description">
              Description
            </label>
            <textarea
              id="skill-description"
              value={description}
              maxLength={SKILL_DESCRIPTION_MAX_LENGTH}
              rows={3}
              onChange={(event) => setDescription(event.target.value)}
              className="mt-1 w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
            />
            <div className="mt-1 flex flex-col gap-1">
              <p className="text-xs text-gray-500">{SKILL_DESCRIPTION_LIMITS_HINT}</p>
              <p
                className={`text-xs ${
                  descriptionLength > SKILL_DESCRIPTION_MAX_LENGTH
                    ? 'text-red-700'
                    : descriptionLength > SKILL_DESCRIPTION_RECOMMENDED_LENGTH
                      ? 'text-amber-700'
                      : 'text-gray-500'
                }`}
              >
                {descriptionLength}/{SKILL_DESCRIPTION_MAX_LENGTH}
                {descriptionWarning ? ` — ${descriptionWarning}` : ''}
              </p>
            </div>
          </div>
          <div>
            <label className="block text-sm font-medium text-gray-700">Body</label>
            <div className="mt-1 min-h-[220px] rounded-md border border-gray-300">
              <LexicalEditor
                ref={editorRef}
                placeholder="Write the skill instructions in markdown..."
                showToolbar
                className="min-h-[220px]"
              />
            </div>
          </div>
        </div>

        {error && (
          <p className="mt-3 rounded-md bg-red-50 px-3 py-2 text-sm text-red-700" role="alert">
            {error}
          </p>
        )}

        <div className="mt-5 flex justify-end gap-2">
          <button
            type="button"
            onClick={onClose}
            className="rounded-md border border-gray-300 px-4 py-2 text-sm text-gray-700 hover:bg-gray-50"
          >
            Cancel
          </button>
          <button
            type="button"
            disabled={saving}
            onClick={handleSave}
            className="rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
          >
            {saving ? 'Saving...' : 'Save skill'}
          </button>
        </div>
      </div>
    </div>
  );
}
