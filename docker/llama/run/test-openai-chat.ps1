param(
    [string]$BaseUrl = 'http://localhost:8110/llama-cpp',
    [string]$Model = 'qwen3.5-27b'
)

$ErrorActionPreference = 'Stop'

$payload = @{
    model = $Model
    messages = @(
        @{
            role = 'user'
            content = 'Reply with exactly: pong'
        }
    )
    max_tokens = 32
} | ConvertTo-Json -Depth 8

Invoke-RestMethod `
    -Uri "$BaseUrl/v1/chat/completions" `
    -Method Post `
    -ContentType 'application/json' `
    -Body $payload
