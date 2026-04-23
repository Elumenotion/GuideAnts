import { ImageUpload } from '../../common/ImageUpload';

interface BasicInfoProps {
  name: string;
  description: string;
  avatarData?: { bytes: number[]; contentType: string } | null;
  currentAvatarUrl?: string;
  onNameChange: (name: string) => void;
  onDescriptionChange: (description: string) => void;
  onAvatarChange: (data: { bytes: number[]; contentType: string } | null) => void;
}

export function BasicInfo({
  name,
  description,
  avatarData,
  currentAvatarUrl,
  onNameChange,
  onDescriptionChange,
  onAvatarChange,
}: BasicInfoProps) {
  return (
    <div className="space-y-6">
      <h2 className="text-lg font-semibold text-gray-900">Basic Information</h2>
      
      <div className="space-y-4">
        {/* Name */}
        <div>
          <label htmlFor="guide-name" className="block text-sm font-medium text-gray-700 mb-1">
            Name <span className="text-red-500">*</span>
          </label>
          <input
            id="guide-name"
            type="text"
            value={name}
            onChange={(e) => onNameChange(e.target.value)}
            placeholder="e.g., Customer Support Guide"
            required
            className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            data-tour-id="guide.general.name.input"
          />
        </div>

        {/* Description */}
        <div>
          <label htmlFor="guide-description" className="block text-sm font-medium text-gray-700 mb-1">
            Description <span className="text-red-500">*</span>
          </label>
          <textarea
            id="guide-description"
            value={description}
            onChange={(e) => onDescriptionChange(e.target.value)}
            placeholder="Describe what this guide does and when to use it..."
            rows={3}
            required
            className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            data-tour-id="guide.general.description.editor"
          />
        </div>

        {/* Avatar */}
        <div data-tour-id="guide.general.avatar.upload">
          <ImageUpload
            label="Avatar"
            value={avatarData}
            currentImageUrl={currentAvatarUrl}
            onChange={onAvatarChange}
          />
        </div>
      </div>
    </div>
  );
}


