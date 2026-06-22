import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import { AuthTab } from '../configTabs/AuthTab';

vi.mock('../../../services/api', () => ({
  api: {
    guides: {
      guides: {
        generateApiKey: vi.fn(),
        removeApiKey: vi.fn(),
      },
    },
  },
}));

const defaultProps = {
  authWebhookUrl: '',
  setAuthWebhookUrl: vi.fn(),
  authWebhookTimeout: 5,
  setAuthWebhookTimeout: vi.fn(),
  friendlyName: '',
  hasApiKey: false,
  sessionApiKey: null,
  guideId: 'guide-1',
  publishedGuideId: 'pub-1',
  onApiKeyChange: vi.fn(),
  onSessionApiKeyChange: vi.fn(),
};

describe('AuthTab', () => {
  it('shows read-only AppIdentity panel and hides webhook/API key controls', () => {
    render(<AuthTab {...defaultProps} authMode="AppIdentity" />);

    expect(screen.getByText(/GuideAnts app identity/i)).toBeInTheDocument();
    expect(screen.getByText(/Managed by the system; cannot be changed here/i)).toBeInTheDocument();
    expect(screen.queryByText('API Key Authentication')).not.toBeInTheDocument();
    expect(screen.queryByText('Webhook Authentication (Advanced)')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Generate API Key/i })).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Authentication Webhook URL')).not.toBeInTheDocument();
  });

  it('shows webhook and API key controls for Webhook auth mode', () => {
    render(
      <AuthTab
        {...defaultProps}
        authMode="Webhook"
        authWebhookUrl="https://example.com/validate"
      />,
    );

    expect(screen.queryByText(/GuideAnts app identity/i)).not.toBeInTheDocument();
    expect(screen.getByText('API Key Authentication')).toBeInTheDocument();
    expect(screen.getByText('Webhook Authentication (Advanced)')).toBeInTheDocument();
    expect(screen.getByLabelText('Authentication Webhook URL')).toBeInTheDocument();
  });

  it('shows API key controls for ApiKey auth mode', () => {
    render(<AuthTab {...defaultProps} authMode="ApiKey" hasApiKey />);

    expect(screen.queryByText(/GuideAnts app identity/i)).not.toBeInTheDocument();
    expect(screen.getByText('API Key Authentication')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Regenerate Key/i })).toBeInTheDocument();
  });
});
