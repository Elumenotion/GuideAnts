import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { LocalCapabilityFrame } from '../LocalCapabilityFrame';

describe('LocalCapabilityFrame', () => {
  it('renders nothing when hidden', () => {
    const { container } = render(
      <LocalCapabilityFrame title="Embeddings" phase="hidden" />,
    );
    expect(container).toBeEmptyDOMElement();
  });

  it('renders loading state with optional refresh', () => {
    const onRefresh = vi.fn();
    render(
      <LocalCapabilityFrame title="Embeddings" phase="loading" onRefresh={onRefresh} />,
    );

    expect(screen.getByText('Embeddings')).toBeInTheDocument();
    expect(screen.getByText('Contacting local service…')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(onRefresh).toHaveBeenCalled();
  });

  it('classifies known unavailable failures into friendly callouts', () => {
    const { rerender } = render(
      <LocalCapabilityFrame
        title="Embeddings"
        phase="error"
        errorMessage="No local embeddings configured for this container yet."
      />,
    );
    expect(screen.getByText('Local runtime unavailable')).toBeInTheDocument();
    expect(screen.getByText(/No local embeddings configured/)).toBeInTheDocument();

    rerender(
      <LocalCapabilityFrame
        title="Embeddings"
        phase="error"
        errorMessage="Upstream does not expose local model operations for this service."
      />,
    );
    expect(screen.getByText(/does not currently have the matching local service server/)).toBeInTheDocument();

    rerender(
      <LocalCapabilityFrame
        title="Embeddings"
        phase="error"
        errorMessage="Connection refused"
        upstream={{
          upstreamTarget: 'http://127.0.0.1:9/embeddings',
          upstreamStatus: 0,
          upstreamStatusText: '',
          upstreamContentType: null,
          upstreamBody: null,
        }}
      />,
    );
    expect(screen.getByText(/placeholder local-runtime URL/)).toBeInTheDocument();

    rerender(
      <LocalCapabilityFrame
        title="Embeddings"
        phase="error"
        errorMessage="Connection refused"
        upstream={{
          upstreamTarget: 'http://localhost:8110',
          upstreamStatus: 0,
          upstreamStatusText: '',
          upstreamContentType: null,
          upstreamBody: null,
        }}
      />,
    );
    expect(screen.getByText(/not reachable right now/)).toBeInTheDocument();
  });

  it('renders upstream failure details for unclassified errors', () => {
    render(
      <LocalCapabilityFrame
        title="Embeddings"
        phase="error"
        errorMessage="Upstream failed"
        upstream={{
          upstreamTarget: 'http://localhost:8110/v1/models',
          upstreamStatus: 502,
          upstreamStatusText: 'Bad Gateway',
          upstreamContentType: 'text/plain',
          upstreamBody: 'gateway timeout',
        }}
      />,
    );

    expect(screen.getByText('Upstream failed')).toBeInTheDocument();
    expect(screen.getByText('http://localhost:8110/v1/models')).toBeInTheDocument();
    expect(screen.getByText(/502 Bad Gateway/)).toBeInTheDocument();
    expect(screen.getByText('text/plain')).toBeInTheDocument();
    expect(screen.getByText('gateway timeout')).toBeInTheDocument();
  });

  it('renders transport diagnostics for local runtime network errors', () => {
    render(
      <LocalCapabilityFrame
        title="Embeddings"
        phase="error"
        errorMessage="Upstream request to http://guideants-ai:80/emb failed: connection refused"
        upstream={{
          upstreamTarget: 'http://guideants-ai:80/emb/ready',
          upstreamStatus: 0,
          upstreamStatusText: 'NetworkError',
          upstreamContentType: '',
          upstreamBody: "docker inspect guideants-ai --format '{{.State.Error}}'",
        }}
      />,
    );

    expect(screen.getByText(/Upstream request to/)).toBeInTheDocument();
    expect(screen.getByText('http://guideants-ai:80/emb/ready')).toBeInTheDocument();
    expect(screen.getByText(/0 NetworkError/)).toBeInTheDocument();
    expect(screen.getByText(/docker inspect guideants-ai/)).toBeInTheDocument();
  });

  it('renders available children', () => {
    render(
      <LocalCapabilityFrame title="Embeddings" phase="available">
        <div data-testid="child">Child content</div>
      </LocalCapabilityFrame>,
    );

    expect(screen.getByTestId('child')).toBeInTheDocument();
  });
});
