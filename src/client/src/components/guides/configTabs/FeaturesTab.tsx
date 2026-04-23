interface FeaturesTabProps {
  showConversationStarters: boolean;
  setShowConversationStarters: (show: boolean) => void;
  showAttachments: boolean;
  setShowAttachments: (show: boolean) => void;
}

export function FeaturesTab({
  showConversationStarters,
  setShowConversationStarters,
  showAttachments,
  setShowAttachments
}: FeaturesTabProps) {
  return (
    <div className="space-y-6">
      <h3 className="text-lg font-medium text-gray-900">Enabled Features</h3>
      
      <div className="space-y-4">
        <div className="flex items-center justify-between py-3 border-b border-gray-100">
          <div>
            <span className="block text-sm font-medium text-gray-700">Conversation Starters</span>
            <span className="block text-xs text-gray-500">Show suggested questions when the conversation is empty</span>
          </div>
          <label className="relative inline-flex items-center cursor-pointer">
            <input 
              type="checkbox" 
              checked={showConversationStarters} 
              onChange={(e) => setShowConversationStarters(e.target.checked)}
              className="sr-only peer" 
            />
            <div className="w-11 h-6 bg-gray-200 peer-focus:outline-none peer-focus:ring-4 peer-focus:ring-blue-300 rounded-full peer peer-checked:after:translate-x-full peer-checked:after:border-white after:content-[''] after:absolute after:top-[2px] after:left-[2px] after:bg-white after:border-gray-300 after:border after:rounded-full after:h-5 after:w-5 after:transition-all peer-checked:bg-blue-600"></div>
          </label>
        </div>

        <div className="flex items-center justify-between py-3 border-b border-gray-100">
          <div>
            <span className="block text-sm font-medium text-gray-700">File Attachments</span>
            <span className="block text-xs text-gray-500">Allow users to upload files (images, docs) to the conversation</span>
          </div>
          <label className="relative inline-flex items-center cursor-pointer">
            <input 
              type="checkbox" 
              checked={showAttachments} 
              onChange={(e) => setShowAttachments(e.target.checked)}
              className="sr-only peer" 
            />
            <div className="w-11 h-6 bg-gray-200 peer-focus:outline-none peer-focus:ring-4 peer-focus:ring-blue-300 rounded-full peer peer-checked:after:translate-x-full peer-checked:after:border-white after:content-[''] after:absolute after:top-[2px] after:left-[2px] after:bg-white after:border-gray-300 after:border after:rounded-full after:h-5 after:w-5 after:transition-all peer-checked:bg-blue-600"></div>
          </label>
        </div>
      </div>
    </div>
  );
}






