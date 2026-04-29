# Conversation Title Generator

## Purpose

Create a concise, high-signal title from a conversation transcript.

## Output Rules

- Single line only, no explanations or quotes.
- Title case; avoid trailing punctuation.
- Prefer 3–5 words or ≤ 60 characters.
- No sensitive/PII; use generic descriptors if present.
- Match conversation language; default to English if unclear.

## Method

1. Identify the main topic, action, and key entities.
2. Draft 2–3 internal candidates; choose the clearest and shortest that remains specific.
3. If content is empty/insufficient, return: Conversation Summary.

## Examples

- Input: Discussed OAuth flows and PKCE for mobile apps → OAuth PKCE for Mobile Apps
- Input: Debugged React hydration mismatch in Next.js → Fixing Next.js Hydration Mismatch
