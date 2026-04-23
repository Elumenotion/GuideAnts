# ScriptExecutionAgent

A lightweight .NET HTTP server that can be injected into any Linux container to provide script execution capabilities via HTTP API.

## Overview

The ScriptExecutionAgent is a bolt-on component that:
- Runs on port 8081 inside any container
- Provides HTTP API for executing Python, Bash, and PowerShell scripts
- Uses the container's native interpreters
- Works with Azure Container Apps (no Docker exec dependency)

## API Endpoints

### POST /execute
Executes a script and returns the result.

**Request:**
```json
{
  "script": "print('Hello, World!')",
  "scriptType": "Python",
  "workingDirectory": "/app/ContentFiles/project-123/notebooks/notebook-456"
}
```

**Response:**
```json
{
  "standardOutput": "Hello, World!\n",
  "standardError": ""
}
```

### GET /health
Health check endpoint.

**Response:**
```
OK
```

### GET /files?directory={path}
Lists files in a directory.

**Response:**
```json
["file1.py", "file2.txt", "output.csv"]
```

## Container Integration

### Option 1: Multi-stage Dockerfile
```dockerfile
# Use ScriptExecutionAgent as base
FROM script-execution-agent AS agent

# Your existing container
FROM your-base-image
COPY --from=agent /app /app/script-agent

# Start both your app and the agent
CMD ["sh", "-c", "dotnet /app/script-agent/ScriptExecutionAgent.dll & your-app-command"]
```

### Option 2: Add to Existing Dockerfile
```dockerfile
FROM your-base-image

# Install .NET runtime
RUN apt-get update && apt-get install -y dotnet-runtime-8.0

# Copy ScriptExecutionAgent
COPY ScriptExecutionAgent/ /app/script-agent/

# Expose agent port
EXPOSE 8081

# Start both services
CMD ["sh", "-c", "dotnet /app/script-agent/ScriptExecutionAgent.dll & your-app-command"]
```

### Option 3: Sidecar Pattern
```yaml
# docker-compose.yml
services:
  your-app:
    image: your-app-image
    # ... your app config
    
  script-agent:
    image: script-execution-agent
    volumes:
      - ./ContentFiles:/app/ContentFiles
    ports:
      - "80:80"
```

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `ASPNETCORE_URLS` | `http://+:80` | URL to listen on |

## Supported Script Types

- **Python**: Uses `python` command
- **Bash**: Uses `bash` command  
- **PowerShell**: Uses `pwsh` command

## Usage Example

```csharp
// In your main API
var scriptExecutionUrl = "http://guideants-ai";
var request = new
{
    Script = "print('Hello from Python!')",
    ScriptType = "Python",
    WorkingDirectory = "/app/ContentFiles/project-123/notebooks/notebook-456"
};

using var httpClient = new HttpClient();
var json = JsonSerializer.Serialize(request);
var content = new StringContent(json, Encoding.UTF8, "application/json");

var response = await httpClient.PostAsync($"{scriptExecutionUrl}/execute", content);
var result = await JsonSerializer.DeserializeAsync<ScriptExecutionResult>(await response.Content.ReadAsStreamAsync());
```

## Building

```bash
# Build the agent image
docker build -t script-execution-agent .

# Or use the build script
./build-script-agent.ps1
```

## Testing

```bash
# Test the agent
./test-script-execution.ps1

# Manual testing
curl http://localhost/health
curl -X POST http://localhost/execute \
  -H "Content-Type: application/json" \
  -d '{"script":"print(\"test\")","scriptType":"Python","workingDirectory":"/tmp"}'
```

## Azure Container Apps

For Azure Container Apps deployment:

1. **Build and push the agent image** to your container registry
2. **Add the agent to your container app** using multi-stage Dockerfile
3. **Configure internal networking** between container apps
4. **Update your main API** to use HTTP instead of Docker exec

## Security Considerations

- The agent runs on internal port 80
- No external access required
- Uses container's native interpreters
- Script execution is isolated to the container
- No file system access outside working directory

## Troubleshooting

### Agent Not Starting
```bash
# Check if .NET runtime is installed
dotnet --version

# Check agent logs
docker logs your-container
```

### Script Execution Fails
```bash
# Check if interpreter is available
python --version
bash --version
pwsh --version

# Check working directory permissions
ls -la /app/ContentFiles/
```

### Network Issues
```bash
# Test connectivity
curl http://container-name:8081/health

# Check container networking
docker network ls
docker network inspect your-network
``` 