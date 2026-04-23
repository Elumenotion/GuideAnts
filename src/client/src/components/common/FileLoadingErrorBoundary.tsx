import { Component, ErrorInfo, ReactNode } from 'react';

interface Props {
  children: ReactNode;
  fileId?: string;
  componentName?: string;
}

interface State {
  hasError: boolean;
  error?: Error;
}

export class FileLoadingErrorBoundary extends Component<Props, State> {
  constructor(props: Props) {
    super(props);
    this.state = { hasError: false };
  }

  static getDerivedStateFromError(error: Error): State {
    return { hasError: true, error };
  }

  componentDidCatch(error: Error, errorInfo: ErrorInfo) {
    console.log('[TELEMETRY] FileLoadingErrorBoundary caught error', {
      fileId: this.props.fileId,
      componentName: this.props.componentName,
      error: error.message,
      errorStack: error.stack,
      componentStack: errorInfo.componentStack,
      timestamp: Date.now()
    });
  }

  render() {
    if (this.state.hasError) {
      return (
        <div className="flex items-center justify-center h-64 text-red-500">
          <div className="text-center">
            <p className="text-lg font-medium">Failed to load file</p>
            <p className="text-sm text-gray-600 mt-2">
              An error occurred while loading this file. Please try again.
            </p>
            {this.props.fileId && (
              <p className="text-xs text-gray-400 mt-1">
                File ID: {this.props.fileId}
              </p>
            )}
            <button
              onClick={() => this.setState({ hasError: false, error: undefined })}
              className="mt-4 px-4 py-2 bg-red-500 text-white rounded hover:bg-red-600"
            >
              Try Again
            </button>
          </div>
        </div>
      );
    }

    return this.props.children;
  }
}





