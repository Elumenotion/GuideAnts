import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { SECRET_MASK } from '../../constants';
import { CoreConnectionStep } from '../CoreConnectionStep';
import { HuggingFaceConnectionStep } from '../HuggingFaceConnectionStep';
import { OpenAiConnectionStep } from '../OpenAiConnectionStep';
import { OpenRouterConnectionStep } from '../OpenRouterConnectionStep';

describe('CoreConnectionStep', () => {
  const onChange = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders foundry connection fields', () => {
    render(
      <CoreConnectionStep
        resource=""
        apiKey=""
        apiVersion=""
        apiKeyHasStoredValue={false}
        errors={{}}
        onChange={onChange}
      />,
    );

    expect(screen.getByText('Microsoft Foundry connection details')).toBeInTheDocument();
    expect(screen.getByLabelText('Resource')).toBeInTheDocument();
    expect(screen.getByLabelText('API key')).toBeInTheDocument();
    expect(screen.getByLabelText('API version')).toBeInTheDocument();
  });

  it('calls onChange for resource, api key, and api version', () => {
    render(
      <CoreConnectionStep
        resource=""
        apiKey=""
        apiVersion=""
        apiKeyHasStoredValue={false}
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('Resource'), { target: { value: 'my-resource' } });
    fireEvent.change(screen.getByLabelText('API key'), { target: { value: 'key-123' } });
    fireEvent.change(screen.getByLabelText('API version'), { target: { value: '2024-11-30' } });

    expect(onChange).toHaveBeenCalledWith({ resource: 'my-resource' });
    expect(onChange).toHaveBeenCalledWith({ apiKey: 'key-123' });
    expect(onChange).toHaveBeenCalledWith({ apiVersion: '2024-11-30' });
  });

  it('shows stored key hint and field validation errors', () => {
    render(
      <CoreConnectionStep
        resource="bad"
        apiKey={SECRET_MASK}
        apiVersion="bad"
        apiKeyHasStoredValue
        errors={{ resource: 'Resource required', apiKey: 'Key required', apiVersion: 'Invalid version' }}
        onChange={onChange}
      />,
    );

    expect(screen.getByText(/A key is already stored/)).toBeInTheDocument();
    expect(screen.getByText('Resource required')).toBeInTheDocument();
    expect(screen.getByText('Key required')).toBeInTheDocument();
    expect(screen.getByText('Invalid version')).toBeInTheDocument();
  });
});

describe('OpenAiConnectionStep', () => {
  const onChange = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders connection fields and heading', () => {
    render(
      <OpenAiConnectionStep
        apiKey=""
        endpoint=""
        apiKeyHasStoredValue={false}
        errors={{}}
        onChange={onChange}
      />,
    );

    expect(screen.getByText('OpenAI connection details')).toBeInTheDocument();
    expect(screen.getByLabelText('API key')).toBeInTheDocument();
    expect(screen.getByLabelText('Endpoint (optional)')).toBeInTheDocument();
  });

  it('calls onChange when api key and endpoint are edited', () => {
    render(
      <OpenAiConnectionStep
        apiKey=""
        endpoint=""
        apiKeyHasStoredValue={false}
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('API key'), { target: { value: 'sk-test' } });
    fireEvent.change(screen.getByLabelText('Endpoint (optional)'), {
      target: { value: 'https://proxy.example/v1' },
    });

    expect(onChange).toHaveBeenCalledWith({ apiKey: 'sk-test' });
    expect(onChange).toHaveBeenCalledWith({ endpoint: 'https://proxy.example/v1' });
  });

  it('shows stored key hint and validation errors', () => {
    render(
      <OpenAiConnectionStep
        apiKey={SECRET_MASK}
        endpoint="bad"
        apiKeyHasStoredValue
        errors={{ apiKey: 'API key required', endpoint: 'Invalid endpoint' }}
        onChange={onChange}
      />,
    );

    expect(screen.getByText(/A key is already stored/)).toBeInTheDocument();
    expect(screen.getByText('API key required')).toBeInTheDocument();
    expect(screen.getByText('Invalid endpoint')).toBeInTheDocument();
  });
});

describe('HuggingFaceConnectionStep', () => {
  const onChange = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders token and router base url fields', () => {
    render(
      <HuggingFaceConnectionStep
        token=""
        routerBaseUrl="https://router.huggingface.co/v1"
        tokenHasStoredValue={false}
        errors={{}}
        onChange={onChange}
      />,
    );

    expect(screen.getByText('Hugging Face connection details')).toBeInTheDocument();
    expect(screen.getByLabelText('Token')).toBeInTheDocument();
    expect(screen.getByLabelText('Router Base URL')).toHaveValue('https://router.huggingface.co/v1');
  });

  it('calls onChange when token and router url are edited', () => {
    render(
      <HuggingFaceConnectionStep
        token=""
        routerBaseUrl=""
        tokenHasStoredValue={false}
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('Token'), { target: { value: 'hf_token' } });
    fireEvent.change(screen.getByLabelText('Router Base URL'), {
      target: { value: 'https://custom.router/v1' },
    });

    expect(onChange).toHaveBeenCalledWith({ token: 'hf_token' });
    expect(onChange).toHaveBeenCalledWith({ routerBaseUrl: 'https://custom.router/v1' });
  });

  it('shows stored token hint and validation errors', () => {
    render(
      <HuggingFaceConnectionStep
        token=""
        routerBaseUrl=""
        tokenHasStoredValue
        errors={{ token: 'Token required', routerBaseUrl: 'Invalid URL' }}
        onChange={onChange}
      />,
    );

    expect(screen.getByText(/A token is already stored/)).toBeInTheDocument();
    expect(screen.getByText('Token required')).toBeInTheDocument();
    expect(screen.getByText('Invalid URL')).toBeInTheDocument();
  });
});

describe('OpenRouterConnectionStep', () => {
  const onChange = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders all connection fields', () => {
    render(
      <OpenRouterConnectionStep
        apiKey=""
        baseUrl="https://openrouter.ai/api/v1"
        httpReferer=""
        appTitle=""
        apiKeyHasStoredValue={false}
        errors={{}}
        onChange={onChange}
      />,
    );

    expect(screen.getByText('OpenRouter connection details')).toBeInTheDocument();
    expect(screen.getByLabelText('API key')).toBeInTheDocument();
    expect(screen.getByLabelText('Base URL')).toHaveValue('https://openrouter.ai/api/v1');
    expect(screen.getByLabelText('HTTP Referer (optional)')).toBeInTheDocument();
    expect(screen.getByLabelText('App Title (optional)')).toBeInTheDocument();
  });

  it('calls onChange for each editable field', () => {
    render(
      <OpenRouterConnectionStep
        apiKey=""
        baseUrl=""
        httpReferer=""
        appTitle=""
        apiKeyHasStoredValue={false}
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('API key'), { target: { value: 'or-key' } });
    fireEvent.change(screen.getByLabelText('Base URL'), { target: { value: 'https://proxy/v1' } });
    fireEvent.change(screen.getByLabelText('HTTP Referer (optional)'), {
      target: { value: 'https://app.example' },
    });
    fireEvent.change(screen.getByLabelText('App Title (optional)'), { target: { value: 'My App' } });

    expect(onChange).toHaveBeenCalledWith({ apiKey: 'or-key' });
    expect(onChange).toHaveBeenCalledWith({ baseUrl: 'https://proxy/v1' });
    expect(onChange).toHaveBeenCalledWith({ httpReferer: 'https://app.example' });
    expect(onChange).toHaveBeenCalledWith({ appTitle: 'My App' });
  });

  it('shows stored key hint and validation errors', () => {
    render(
      <OpenRouterConnectionStep
        apiKey=""
        baseUrl=""
        httpReferer=""
        appTitle=""
        apiKeyHasStoredValue
        errors={{ apiKey: 'Required', baseUrl: 'Bad URL' }}
        onChange={onChange}
      />,
    );

    expect(screen.getByText(/A key is already stored/)).toBeInTheDocument();
    expect(screen.getByText('Required')).toBeInTheDocument();
    expect(screen.getByText('Bad URL')).toBeInTheDocument();
  });
});
