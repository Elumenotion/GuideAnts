# AntRunner.Chat

AntRunner.Chat is a .NET library that lets you easily create and manage conversations with tool-based AI assistants. These AI agents can help you by answering questions, running tools, or performing tasks all through a simple chat interface.

## What can you do with AntRunner.Chat?

- Converse naturally with AI assistants powered by OpenAI models like GPT-4o and gpt-4.1-mini.
- Manage multi-turn conversations with context.
- Switch between different AI assistants on the fly.
- Undo the last message if you want to change something.
- Save and load conversations to keep your chat history.
- Track token usage to monitor your API consumption.

## Getting Started

### Install the NuGet Package

You can add AntRunner.Chat to your .NET project using the NuGet package manager:

```bash
dotnet add package AntRunner.Chat --version 0.6.1
```

Or, add this directive in your C# notebook or script:

```csharp
#r "nuget: AntRunner.Chat, 0.6.1"
```

## Basic Usage Example

Here is a simple example showing how to start a conversation with an AI assistant and send messages:

```csharp
using System.Threading.Tasks;
using AntRunner.Chat;

// Load your environment variables and AI service configuration (example method)
var envVariables = Settings.GetEnvironmentVariables();
foreach (var kvp in envVariables)
{
    Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);
}

var config = new AzureOpenAiConfig
{
    ResourceName = "<azure-openai-resource>",
    ApiKey = "<api-key>",
    ApiVersion = "<api-version>",
    DeploymentId = "<deployment-id>",
};

// Set up the chat options with assistant name and model deployment
static ChatRunOptions chatConfiguration = new()
{
    AssistantName = "Python Ants",
    DeploymentId = "gpt-4.1-mini",
};

// Create a new conversation instance
var conversation = await Conversation.Create(chatConfiguration, config);

// Convenience method to send a message and get a response
async Task<ChatRunOutput> Chat(string message)
{
    chatConfiguration.Instructions = message;
    var runnerOutput = await conversation.Chat(message);
    runnerOutput.LastMessage.DisplayAs("text/markdown");
    return runnerOutput;
}

// Example usage: send a message
var output = await Chat("Hello AI! What can you do?");
```

## More Features

### Continue the Conversation

You can keep chatting by sending more messages:

```csharp
var output = await Chat("Tell me about the prerequisites for setting up .NET 8 SDK and Docker.");
```

### Undo the Last Message

If you want to remove the last message from the conversation:

```csharp
conversation.Undo();
```

### Switch Assistants

Change to a different AI assistant anytime:

```csharp
await conversation.ChangeAssistant("Web Ants");
var output = await Chat("What events are happening next week in my city?");
```

### Save and Load Conversations

Save your conversation to a file:

```csharp
conversation.Save("./savedConversation.json");
```

Load a conversation from a saved file:

```csharp
var conversation2 = Conversation.Create(@"./savedConversation.json", config);
```

### View Conversation and Usage Stats

See the last message or the full dialog:

```csharp
var lastMessage = conversation.LastResponse.LastMessage;
var fullDialog = conversation.LastResponse.Dialog;
```

Check token usage for the entire conversation or a single turn:

```csharp
var usage = conversation.Usage;
var lastTurnUsage = output.Usage;
```

---

## Container Sandboxes

A major feature of AntRunner.Chat is the use of container sandboxes to provide isolated environments for different AI assistants and tools. These sandboxes are implemented as Docker containers, enabling you to run services such as the .NET server, Python environments with or without CUDA support, PlantUML, and more.

### Available Containers

- **dotnet-server**: The main .NET service container.
- **guideants-ai**: Consolidated Python 3.11 + llama-server environment with .NET 9 runtime and CUDA support.
- **plantuml**: Container for PlantUML diagram rendering.
- **qdrant**: Vector search engine container.
- **kernel-memory**: Custom service container for kernel memory management.

### Setup Guide and Build Scripts

To help you get started with these containers, we provide a comprehensive [Setup Guide](./setup_guide.md) that covers all prerequisites and detailed instructions for building the local Docker images.

You can use the provided build scripts:

- `build_local_images.sh` for Linux/macOS
- `build_local_images.ps1` for Windows PowerShell

These scripts will prompt you to select whether to build the CPU-only or CUDA-enabled Python images and will build all necessary images in the correct order.

Make sure to follow the setup guide to prepare your environment and build the containers before running the solution.

---

## Summary

AntRunner.Chat makes it simple to build powerful AI chat experiences in your .NET applications. Whether you want to create helpful assistants, automate tasks, or build interactive tools, AntRunner.Chat provides an easy-to-use interface to OpenAI-powered conversations.

---

If you have questions or want to contribute, feel free to open an issue or pull request!
