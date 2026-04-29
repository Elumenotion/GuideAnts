## Introduction: Why Team Guides & Assistants?

Team Guides & Assistants are the core of Guidance Notebooks, designed to let your team move beyond generic AI chat and into structured, scalable, and transparent workflows. They capture your organization’s knowledge, standardize best practices, and embed business context into every conversation. 

> "The Team Guides and Assistants feature is arguably the most important for teams using AI to collaborate."

This system is built so you can:

- Codify and share "how we work here" as interactive, intelligent tools.
- Empower everyone on your team with AI that knows your context and adapts as your needs evolve.
- Start with out-of-the-box guides and assistants, but unlock even more value by building your own to fit your unique workflows and integrations.

# Team Guides & Assistants - User Guide

## Table of Contents

1. [Overview](#overview)
2. [Key Concepts](#key-concepts)
3. [Getting Started](#getting-started)
4. [Working with Guides](#working-with-guides)
5. [Working with Assistants](#working-with-assistants)
6. [Managing Crews](#managing-crews)
7. [Advanced Features](#advanced-features)
8. [Export & Import](#export--import)
9. [Best Practices](#best-practices)
10. [Troubleshooting](#troubleshooting)

---

## Overview

### What are Team Guides & Assistants?

**Team Guides & Assistants** is a powerful feature that enables project owners to create and manage AI-powered team resources within their projects. This system provides two complementary types of AI entities:

- **Guides** - Primary AI entities that can work standalone or orchestrate crews of assistants to accomplish complex tasks
- **Assistants** - Specialized AI entities designed to be members of guide crews, each with specific skills and knowledge

### Why Use This Feature?

Team Guides & Assistants help you:

- **Standardize Workflows** - Create consistent AI-powered processes for your team
- **Scale Expertise** - Capture domain knowledge and make it accessible to everyone
- **Improve Collaboration** - Enable multiple AI agents to work together on complex tasks
- **Boost Productivity** - Provide team members with ready-to-use AI tools tailored to your needs
- **Maintain Quality** - Ensure consistent outputs through pre-configured instructions and tools

### Who Can Use This Feature?

- **Project Owners** (Team Owners) have full access to create, edit, and delete guides and assistants
- All other project members can use the guides and assistants created by owners (in notebooks and conversations)

---

## Key Concepts

### Guides

A **Guide** is your primary AI entity that:

- Works independently or leads a crew of assistants
- Has a home page with markdown documentation
- Can be exported and shared across teams
- Configures the AI model, tools, and behavior
- Provides context and instructions for specific tasks

**Example Use Cases:**

- "Research Assistant" - Searches the web, analyzes data, and produces reports
- "Code Reviewer" - Analyzes code quality, suggests improvements, coordinates multiple specialized reviewers
- "Customer Support" - Answers questions using your knowledge base and escalates to specialists

### Assistants

An **Assistant** is a specialized AI entity that:

- Becomes usable when assigned to a guide's crew
- Has specific skills, tools, and knowledge
- Can be reused across multiple guides
- Does not have a home page or its own crew

**Example Use Cases:**

- "Python Expert" - Specialized in Python code analysis and debugging
- "Security Auditor" - Focuses on security vulnerabilities and best practices
- "Documentation Writer" - Converts technical information into user-friendly docs

### Crews

A **Crew** is a team of assistants that work together under a guide's direction:

- Defined within a guide (not a separate entity)
- Can include both custom assistants and global assistants
- Members execute specialized tasks as directed by the guide
- Order of crew members can affect task delegation

**How Crews Work:**

1. Guide receives a task/question
2. Guide analyzes what needs to be done
3. Guide delegates subtasks to appropriate crew members
4. Assistants execute their specialized tasks
5. Guide synthesizes results and provides the final answer

### Global vs. Custom Assistants

**Global Assistants:**

- Pre-configured by the platform
- Available to all teams
- Include: Search (Web Search and Scrape), Code Executor, Code and Data, Memory Explorer, Media Creator, Diagrams, Exchange Calendar, Exchange Mail, OneNote, Read Web, Discovery, Narrative, Planning, Sizing, Conversation Title Generator
- Cannot be modified but can be added to crews

**Custom Assistants:**

- Created by your team
- Tailored to your specific needs
- Can be configured with custom tools, files, and instructions
- Only available to your team

---

## Getting Started

### The New User Experience: First Steps

When you visit a project for the first time as an owner, you’ll see the **Team Guides** link in the sidebar. If you haven’t created any guides or assistants yet, you’ll see empty panels and clear prompts to create or import resources. 

Guidance Notebooks provides several ready-to-use global guides and assistants, so you can get started quickly. However, to truly tailor the AI to your team’s processes, or to integrate with your own tools and systems, you’ll want to create custom guides and assistants. 

Importing allows you to leverage guides or assistants made by others, multiplying your team’s capabilities and sharing best practices across projects.




### Accessing Team Guides

1. **Navigate to Any Project**

- Open a project where you are the owner

2. **Open the Team Guides Dashboard**

- In the left sidebar, locate and click **"Team Guides"**
- This link appears below the Invitations section
- Only visible to project owners

3. **Explore the Dashboard**

- **Guides Tab** - View and manage your guides
- **Assistants Tab** - View and manage your assistants
- **Search Bar** - Filter by name, description, or model
- **Create Buttons** - Quick access to create new guides or assistants
- **Import Button** - Import guides from .zip packages

### Dashboard Overview

The dashboard provides a clean, organized view of your team's AI resources:

**Guide Cards Display:**

- Guide name and description
- Avatar (custom or gradient placeholder)
- AI model being used
- Number of tools configured
- Number of crew members
- "Standalone" badge if no crew
- Action buttons: Edit, Duplicate, Export, Delete

**Assistant Cards Display:**

- Assistant name and description
- Avatar (custom or gradient placeholder)
- AI model being used
- Number of tools configured
- Crew memberships (which guides use this assistant)
- "Not assigned" badge if not in any crews
- Action buttons: Edit, Duplicate, Delete

---

## Working with Guides

### Creating a New Guide

#### Step 1: Initiate Creation

1. Click **"Create Guide"** button in the dashboard header
2. You'll be taken to the Guide Editor

#### Step 2: General Information

**Basic Info:**

- **Name** (required) - Must be unique within your team
- Example: "Market Research Assistant"
- **Description** (required) - Explain what this guide does
- Example: "Analyzes market trends, competitor activity, and generates comprehensive research reports"
- **Avatar** (optional) - Upload a custom image (PNG, JPG, or GIF)
- Click the avatar placeholder to upload
- Ideal size: 200x200px or larger (will be resized automatically)

**Instructions:**

- Write detailed instructions for how the guide should behave
- Use the rich text editor (Lexical) with formatting options
- Include:
- The guide's role and purpose
- Step-by-step procedures
- Tone and style guidelines
- What to do and what to avoid
- Example interactions

**Home Page:**

- Create a markdown landing page for this guide
- This appears when users start a conversation with the guide
- Use to:
- Introduce the guide's capabilities
- Provide usage examples
- List conversation starters
- Document best practices

#### Step 3: Configuration

**Model Selection:**

- Choose an AI model from the catalog
- Available models:
- **GPT-4.1** - Flagship model for deep reasoning, creativity, and enterprise-grade performance
- **GPT-4.1-mini** - Cost-effective, fast GPT-4.1 for high-volume requests
- **GPT-4o** - Multimodal model for text, image, and audio with premium performance
- **GPT-4o-mini** - Ultra-efficient GPT-4o variant for low-latency or budget-critical scenarios
- **GPT-5** - Next-generation AI with expanded intelligence
- **GPT-5-chat** - GPT-5 tuned for conversational experiences
- **GPT-5-mini** - Lean, speedy GPT-5 for high-throughput, cost-sensitive operations
- **o3** - Deep reasoning/coding/vision specialist for analytic and technical tasks
- **o4-mini** - Compact, efficient model for math, coding, and real-time tasks

**Model Configuration:**

- **Temperature** (0.0 - 2.0)
- Lower = More focused and deterministic
- Higher = More creative and varied
- Default: 1.0
- Recommended: 0.7 for factual tasks, 1.2 for creative tasks
- **Top P** (0.0 - 1.0)
- Controls diversity of responses
- Lower = More focused on likely outputs
- Higher = More diverse options considered
- Default: 1.0
- Recommended: Keep at 1.0 unless experimenting
- **Reasoning Effort** (Low, Medium, High)
- Only applicable to reasoning models (o3, o4-mini, gpt-5, gpt-5-chat, gpt-5-mini)
- Controls depth of reasoning process
- Default: Medium
- Note: Automatically adjusts when selecting reasoning models

**Context Options:**

- Define key-value pairs that provide context to the AI
- Keys must be unique and follow naming conventions
- Values can be:
- **Static text** - Fixed values
- **Smart functions** - Dynamic values using special syntax:
    - `[@currentDate]` - Current date
    - `[@userName]` - Name of the user interacting with the guide
    - `[@userEmail]` - Email of the user
- **Empty/blank** - Leave value empty; automatically enables "Set Context Options" tool for users to provide values

**Example Context Options:**

```
company_name = "Acme Corporation"
reporting_format = "Executive summary followed by detailed analysis"
target_audience = (leave empty - user will be prompted by the guide if its instructions include knoweldge of it and instructions to ask for and use the information)
report_date = [@currentDate]
analyst_name = [@userName]
```

#### Step 4: Tools & Authentication

**Selecting Tools:**

- Browse the global tools catalog
- Tools are organized by category:
- **Search** - Read Web, Search Notebook, Search Project
- **Code** - Run Bash, Run Python, Make Diagram
- **Media** - Generate Image, Generate Podcast, Generate Video, Edit Image, Video From Image
- **Integration** - Set Context Options

**Custom OpenAPI Tools:**

- Upload custom API specifications
- Define operations the guide can call
- Configure authentication:
- **OAuth** - For user-delegated permissions
- **Service HTTP** - For API keys and service tokens
- **API Key** - Use Service HTTP with header name and key

**Tool Configuration Tips:**

- Only enable tools the guide needs (reduces confusion)
- Test custom tools in isolation first
- Document required scopes for OAuth tools
- Use descriptive names for custom tools

**Note:** The "Set Context Options" tool is automatically enabled when you have context options with empty values. You don't need to manually select it.

#### Step 5: Files & Knowledge

**Vector Stores:**

- Upload documents for semantic search
- Supported formats: PDF, DOCX, TXT, MD, etc.
- The system automatically:
- Extracts text content
- Converts to searchable markdown
- Indexes for fast retrieval
- Best for: Knowledge bases, documentation, research papers

**Code Interpreter Files:**

- Upload files for the guide to analyze or process
- Supported formats: CSV, JSON, XLSX, Python scripts, etc.
- The guide can:
- Read and analyze data
- Execute code against these files
- Generate visualizations
- Best for: Datasets, configuration files, sample code

**File Management:**

- View upload status and markdown extraction progress
- Files show status badges:
- **Pending** - Waiting for processing
- **Processing** - Currently being converted
- **Completed** - Ready to use
- **Failed** - Issue occurred (hover for details)
- **Skipped** - Not processed (binary files, etc.)

#### Step 6: Conversation Starters

- Define suggested prompts users can click to start conversations
- Each starter should:
- Be clear and actionable
- Demonstrate a key capability
- Use realistic examples
- Limit to 3-5 starters for best UX
- Order matters (shown in the order you define)

**Example Starters:**

```
1. "Analyze the latest market trends for [industry]"
2. "Generate a competitive analysis report for [company]"
3. "What are the emerging technologies in [field]?"
```

#### Step 7: Crew Management

**Creating a Crew:**

- Use the transfer list interface
- Left panel shows available assistants:
- **Custom Assistants** - Your team's assistants
- **Global Assistants** - Platform-provided assistants
- Select assistants and move to the right panel (Selected Members)
- Reorder members by dragging (order affects delegation)

**When to Use Crews:**

- **Complex tasks** requiring multiple specializations
- **Multi-step workflows** that benefit from divide-and-conquer
- **Quality assurance** where one agent reviews another's work
- **Parallel processing** of independent subtasks

**When to Use Standalone:**

- **Simple, focused tasks** where specialization isn't needed
- **Direct question-answer** scenarios
- **Single-domain expertise** already covered by guide's instructions

**Crew Best Practices:**

- 2-5 members is optimal (too many creates coordination overhead)
- Give each assistant a clear, distinct role
- Order members by workflow sequence when possible
- Test with and without crew to compare results

#### Step 8: Save and Test

1. Click **"Save Guide"** in the header
2. If creating new, you'll be redirected to edit mode with the guide ID
3. You can now:

- Continue editing
- Export the guide
- Test it in a notebook conversation

### Editing an Existing Guide

1. From the dashboard, click a guide card or click the **Edit** button
2. Make your changes in any of the tabs:

- **General** - Name, description, avatar, instructions, home page
- **Configuration** - Model, parameters, context options
- **Tools** - Tool selection and custom APIs
- **Files** - Upload or remove files
- **Crew** - Manage crew members

3. Changes are saved when you click **"Save Guide"**
4. Browser will warn you about unsaved changes if you navigate away

### Duplicating a Guide

1. From the dashboard, click the **Duplicate** button on a guide card
2. A copy is created with "Copy of [Original Name]" as the name
3. The duplicate includes:

- All instructions and configuration
- All tools and files
- All crew members
- All context options and conversation starters

4. Edit the duplicate to customize it for a different use case

### Exporting a Guide

1. From the dashboard, click the **Export** button on a guide card
2. Or, from the editor, click the **Export** button in the header
3. A .zip file is downloaded with structure:

```
   guide-[name]-[timestamp].zip
   ├── manifest.json (guide metadata)
   ├── instructions.md (guide instructions)
   ├── home.md (home page content)
   ├── avatar.png (if custom avatar)
   ├── conversationStarters.json
   ├── contextOptions.json
   ├── crews/
   │   └── [crewName].json
   ├── assistants/ (custom assistants only)
   │   └── [assistantName]/
   │       ├── manifest.json
   │       ├── instructions.md
   │       ├── avatar.png
   │       └── ... (files, tools, etc.)
   └── auth.json (authentication configs)
```

4. Global assistants are referenced by name (not exported)
5. Use exports to:

- Share guides with other teams
- Back up important configurations
- Version control your guides (store in Git)

### Deleting a Guide

1. From the dashboard, click the **Delete** button on a guide card
2. Confirm the deletion in the dialog
3. **What Gets Deleted:**

- The guide itself
- The guide's crew definition
- File attachments specific to this guide

4. **What Remains:**

- Assistants (they can be used in other guides)
- Global assistants (platform resources)

5. **Warning:** This action cannot be undone

---

## Working with Assistants

> **Assistants: Modular, Reusable Expertise**
> >
> Assistants are focused specialists, reusable across guides. Build them for repeatable skills, domain-specific logic, or integrations. Keep assistants narrowly focused but reusable. Avoid duplicating generic assistants—each should have a unique expertise or function.




### Creating a New Assistant

Creating an assistant is similar to creating a guide, but simpler (no home page, no crew management):

#### Step 1: Initiate Creation

1. Click **"Create Assistant"** button in the dashboard header
2. You'll see the info banner:
   > "Assistants are optional team members that can be assigned to guide crews. Create them to build specialized agents with specific skills and knowledge that can work together as part of a guide's crew."

#### Step 2: General Information

**Basic Info:**

- **Name** (required) - Unique within your team
- Example: "Python Code Analyzer"
- **Description** (required) - What this assistant specializes in
- Example: "Analyzes Python code for bugs, performance issues, and style violations"
- **Avatar** (optional) - Upload a custom image

**Instructions:**

- Define the assistant's specialized role
- Include:
- Specific skills and expertise
- How it should analyze/process information
- Output format preferences
- Limitations and boundaries

**Example Instructions for a Code Analyzer:**

```
You are a Python code analysis specialist. When given code:

1. Check for syntax errors and logical bugs
2. Identify performance bottlenecks
3. Verify PEP 8 style compliance
4. Suggest refactoring opportunities
5. Highlight security concerns

Format your response as:
- Summary of key issues
- Detailed findings by category
- Specific recommendations with code examples
- Priority rating for each issue (Critical/High/Medium/Low)

Be concise but thorough. Always provide actionable feedback.
```

#### Step 3: Configuration

Same as guides:

- Select AI model
- Configure temperature, topP, reasoning effort
- Define context options

#### Step 4: Tools & Authentication

Same as guides:

- Select global tools
- Upload custom OpenAPI specs
- Configure authentication

**Tool Selection Tips for Assistants:**

- Choose tools specific to this assistant's role
- If the assistant analyzes code, enable Code Interpreter
- If the assistant needs current information, enable Search
- If the assistant creates diagrams, enable Diagrams tool

#### Step 5: Files & Knowledge

Same as guides:

- Upload documents to Vector Stores
- Upload files for Code Interpreter
- Monitor processing status

**When to Add Files:**

- **Standards/Guidelines** - Upload company coding standards, style guides
- **Reference Documentation** - API docs, language references
- **Examples** - Sample code, templates, best practices
- **Knowledge Base** - Domain-specific information

#### Step 6: Conversation Starters

Same as guides:

- Define 3-5 suggested prompts
- Make them specific to the assistant's expertise

**Example Starters for Code Analyzer:**

```
1. "Analyze this Python function for bugs and performance issues"
2. "Review this module for PEP 8 compliance"
3. "Identify security vulnerabilities in this code"
```

#### Step 7: Save and Use

1. Click **"Save Assistant"** in the header
2. The assistant is now available to add to guide crews
3. To use it:

- Edit a guide
- Go to the Crew tab
- Add this assistant to the crew

### When to Create Custom Assistants

**Create Custom Assistants When:**

- You need specialized domain expertise not covered by global assistants
- You have proprietary tools or APIs to integrate
- You need access to company-specific knowledge bases
- You want consistent outputs using specific instructions
- Multiple guides need the same specialized capability

**Use Global Assistants When:**

- The functionality is general-purpose (search, code execution, etc.)
- You don't need custom configuration
- You want to minimize maintenance
- The assistant's role is straightforward

### Editing, Duplicating, and Deleting Assistants

**Editing:**

1. Click the **Edit** button on an assistant card
2. Make changes in any tab (same tabs as guides, minus Home Page and Crew)
3. Click **"Save Assistant"**

**Duplicating:**

1. Click the **Duplicate** button on an assistant card
2. A copy is created with "Copy of [Original Name]" as the name
3. Edit to customize for a different specialization

**Deleting:**

1. Click the **Delete** button on an assistant card
2. Confirm deletion
3. **Note:** If the assistant is used in guide crews, it will be removed from those crews
4. The guides themselves are not affected (they can still work standalone or with remaining crew members)

---

## Managing Crews

> **Crew Design: Why It Matters**
> >
> Crews let you build true "multiprocessing" AI workflows. Assign distinct roles and order of operation for efficiency and transparency. Good crew design reduces error and streamlines processes; poor design can lead to inefficiency or duplication.




### Understanding Crew Dynamics

Crews enable collaborative AI work where:

- The **guide** acts as the coordinator/leader
- **Assistants** are specialists executing subtasks
- The guide delegates based on assistant capabilities
- Results are synthesized by the guide

### Creating Effective Crews

**1. Define Clear Roles**

Each crew member should have a distinct, non-overlapping responsibility:

- ❌ Bad: "Code Expert", "Programming Specialist" (too similar)
- ✅ Good: "Python Analyzer", "Security Auditor", "Performance Optimizer"

**2. Size Matters**

Optimal crew sizes:

- **1 member** - Simple specialization (e.g., guide + one expert)
- **2-3 members** - Most common, handles moderate complexity
- **4-5 members** - Complex workflows with multiple domains
- **6+ members** - Rarely needed, can create coordination overhead

**3. Order Appropriately**

Member order can affect delegation:

- **Sequential workflows**: Order by process steps
- Example: "Data Collector" → "Data Analyzer" → "Report Writer"
- **Parallel workflows**: Order by priority/importance
- Example: "Core Functionality Reviewer" → "Security Auditor" → "Documentation Checker"

**4. Balance Specificity**

- Too narrow: Assistant only handles rare edge cases
- Too broad: Assistant overlaps with guide or other members
- Just right: Clear expertise area with frequent usage

### Example Crew Configurations

#### Example 1: Code Review Guide

**Guide:** "Code Review Lead"
**Crew:**

1. "Syntax & Logic Checker" (Global: Code Executor)
2. "Security Auditor" (Custom: Security best practices)
3. "Performance Analyst" (Custom: Performance patterns)
4. "Documentation Reviewer" (Custom: Doc standards)

**Workflow:**

- User submits code
- Guide analyzes structure
- Delegates syntax checking to member 1
- Delegates security scan to member 2
- Delegates performance analysis to member 3
- Delegates doc review to member 4
- Guide synthesizes findings into coherent report

#### Example 2: Research Assistant Guide

**Guide:** "Market Research Coordinator"
**Crew:**

1. "Web Researcher" (Global: Search)
2. "Data Analyst" (Global: Code Executor)
3. "Competitive Intelligence" (Custom: Industry knowledge)
4. "Report Generator" (Custom: Company report formats)

**Workflow:**

- User requests market analysis
- Guide delegates web research to member 1
- Member 2 analyzes collected data
- Member 3 provides competitive insights
- Member 4 formats final report
- Guide ensures completeness and quality

#### Example 3: Customer Support Guide

**Guide:** "Support Coordinator"
**Crew:**

1. "Knowledge Base Search" (Global: Search + Custom: KB access)
2. "Technical Specialist" (Custom: Product expertise)
3. "Account Manager" (Custom: Account systems integration)

**Workflow:**

- Customer submits question
- Guide checks if it's simple (answers directly)
- For complex issues, delegates KB search to member 1
- Technical questions go to member 2
- Account issues go to member 3
- Guide provides unified response

### Adding/Removing Crew Members

**To Add a Member:**

1. Edit the guide
2. Go to the **Crew** tab
3. Find the assistant in the left panel (Available)
4. Click to select and move to the right panel (Selected Members)
5. Drag to reorder if needed
6. Save the guide

**To Remove a Member:**

1. Edit the guide
2. Go to the **Crew** tab
3. Find the assistant in the right panel (Selected Members)
4. Click the remove (X) button
5. Save the guide

**To Reorder Members:**

1. Edit the guide
2. Go to the **Crew** tab
3. In the right panel, drag members up or down
4. Save the guide

### Testing Crew Performance

After creating a crew:

1. Create a notebook conversation using this guide
2. Test with various prompts that should trigger different members
3. Observe which assistants are invoked for each task
4. Refine:

- Instructions (guide and assistants)
- Crew member selection
- Member order
- Context options

**Signs of Good Crew Design:**

- Tasks are appropriately delegated
- Results are comprehensive and coherent
- No obvious gaps in coverage
- No excessive duplication of effort

**Signs of Poor Crew Design:**

- Guide handles most tasks itself (crew underutilized)
- Multiple members perform similar work (role overlap)
- Important tasks not delegated (missing specialization)
- Results lack coherence (poor synthesis)

---

## Advanced Features

### Context Options in Detail

> **Why Context Options Are Powerful**
> >
> They build persistent, personalized memory per user/project—enabling truly adaptive, context-aware guidance. They greatly reduce repetitive questions and enable seamless multi-assistant/cross-conversation handoff.


Context options provide dynamic, contextual information to guides and assistants.

**Static Values:**

```
company_name = "Acme Corporation"
fiscal_year = "2025"
department = "Engineering"
```

**Smart Functions:**

```
current_date = [@currentDate]
user = [@userName]
contact_email = [@userEmail]
```

**User-Provided Values (Empty/Blank):**

```
project_name = (leave blank)
target_market = (leave blank)
```

When you leave values empty, the system automatically enables the "Set Context Options" tool. Users will use this tool at conversation start to provide the missing values.

**Combination Example:**

```
report_title = "Q1 Market Analysis"
report_date = [@currentDate]
prepared_by = [@userName]
prepared_for = (leave blank)  ← User provides via "Set Context Options" tool
company = "Acme Corporation"
department = "Strategy"
```

**Best Practices:**

- Use static values for stable information
- Use smart functions for dynamic, always-current data
- Leave values blank for per-conversation customization (auto-enables "Set Context Options" tool)
- Keep keys descriptive (use underscores for multi-word)
- Document what each option controls in the guide's home page

### Custom OpenAPI Tools

> **Why Custom OpenAPI Tools Matter**
> >
> Web Connectors let your guides and assistants interact with any API-enabled system, internal or external. This enables automation, integration, and real-time data access, making your AI truly part of your business processes.


Custom tools allow your guides to interact with external APIs.

**Upload Process:**

1. Prepare an OpenAPI 3.0+ specification (JSON or YAML)
2. In the Tools tab, go to Custom OpenAPI section
3. Click **"Upload OpenAPI Spec"**
4. Select your file
5. Review parsed operations
6. Configure authentication if needed
7. Save

**Authentication Configuration:**

**OAuth:**

- Use when API requires user-delegated access
- Configure:
- Client ID
- Tenant (for Azure AD)
- Scopes (space-separated)
- Users will authenticate when first using the tool

**Service HTTP (API Key):**

- Use for API keys, bearer tokens, service credentials
- Configure:
- Header name (e.g., "X-API-Key", "Authorization")
- Value template (can include environment variables)
- Key is used for all users

**Example Configurations:**

```yaml
# GitHub API
authType: oauth
clientId: "your-github-app-id"
scopes: "repo read:user"

# Custom Internal API
authType: service_http
headerName: "Authorization"
valueTemplate: "Bearer {{SERVICE_API_KEY}}"

# Weather API
authType: service_http
headerName: "X-API-Key"
valueTemplate: "{{WEATHER_API_KEY}}"
```

**Managing Operations:**

After uploading an OpenAPI spec, you can manage individual operations:

**Adding New Operations:**

1. Expand the OpenAPI schema in Web Connectors
2. Click **"Add Operation"**
3. The operation editor opens immediately
4. Configure the operation:

- Path (e.g., `/users/{id}`)
- HTTP Method (GET, POST, PUT, DELETE, etc.)
- Parameters (path, query, body)
- Response schemas
- Summary and description

5. Click **"Save to Schema"**
6. Operation appears in the Tools list with "Unsaved" badge
7. Click **Save** at the top to persist to database

**Editing Existing Operations:**

1. Expand the OpenAPI schema in Web Connectors
2. Click the **Edit** button (✏️) on an operation
3. Modify the schema fragment as needed
4. Click **"Save Changes"**
5. Changes sync to the schema automatically
6. Click **Save** at the top to persist

**Deleting Operations:**

1. Click the **Delete** button (🗑️) on an operation
2. Confirm deletion
3. Operation is removed from schema
4. Click **Save** at the top to persist

**Important Notes:**

- New operations won't have edit/delete buttons until the guide is saved
- All operation changes require saving the guide to persist
- The form will warn you about unsaved changes
- Operations with amber background are unsaved

**Testing Custom Tools:**

1. Create a simple guide that uses only this tool
2. Test with various operations
3. Verify authentication works
4. Check error handling
5. Once stable, add to production guides

### File Processing & Markdown Extraction

> **Why Upload Files as Vector Stores?**
> >
> This integrates evolving or large sources of truth directly into the AI’s queryable "long-term memory." Update docs as needed—no need to cram everything into the guide’s prompt.


When you upload files to Vector Stores, the system automatically:

1. **Extracts Content**

- Reads text from PDFs, Word docs, etc.
- Preserves structure (headings, lists, tables)

2. **Converts to Markdown**

- Creates a searchable markdown representation
- Links to original file for reference

3. **Indexes for Search**

- Enables semantic search across content
- Guide/assistant can query relevant sections

**Status Indicators:**

- **Pending** - File uploaded, waiting for processing
- **Processing** - Extraction in progress
- **Completed** - Ready to use (markdown available)
- **Failed** - Error occurred (check error message)
- **Skipped** - Binary file or unsupported format (still attached, not indexed)

**File Type Recommendations:**

| File Type | Vector Store | Code Interpreter |
|----------|----------|----------|
| PDF      | ✅ Excellent | ❌ Limited |
| DOCX     | ✅ Excellent | ❌ Limited |
| TXT/MD   | ✅ Excellent | ✅ Good   |
| CSV      | ❌ Basic  | ✅ Excellent |
| JSON     | ❌ Basic  | ✅ Excellent |
| XLSX     | ❌ Limited | ✅ Excellent |
| Python   | ❌ Basic  | ✅ Excellent |
| Images   | ✅ OCR (text extraction) | ✅ Visual analysis |




**Size Limits:**

- Maximum file size: 100 MB per file (configurable)
- Total storage per guide/assistant: 1 GB (configurable)
- Processing time: 1-30 seconds depending on size and complexity

### Temperature and Creativity Control

> **Tuning for Quality and Speed**
> >
> Adjust temperature and model selection for the right mix of creativity, determinism, and cost. Use higher temperature for brainstorming, lower for precise, repeatable tasks.


Understanding how to tune your model for different scenarios:

**Temperature Scale (0.0 - 2.0):**

```
0.0 ─────── 0.7 ─────── 1.0 ─────── 1.5 ─────── 2.0
│           │           │           │           │
Deterministic   Balanced    Creative    Very Creative   Experimental
```

**Use Cases by Temperature:**

**0.0 - 0.3: Deterministic**

- Factual Q&A
- Data extraction
- Classification
- Code completion
- Consistent outputs critical

**0.4 - 0.7: Focused**

- Technical writing
- Analysis and reports
- Problem-solving
- Most business applications

**0.8 - 1.2: Balanced**

- Content creation
- Brainstorming
- General conversation
- Default for most cases

**1.3 - 1.7: Creative**

- Marketing copy
- Storytelling
- Novel ideas
- Alternative approaches

**1.8 - 2.0: Experimental**

- Fiction writing
- Artistic projects
- Unconventional solutions
- Exploration

**Top P (Nucleus Sampling):**

- Usually keep at 1.0 (considers all options weighted by probability)
- Lower values (0.8-0.9) make output more focused
- Rarely need to adjust unless experimenting

**Reasoning Effort (for reasoning models):**

- **Low**: Quick reasoning, faster responses
- **Medium**: Balanced (default)
- **High**: Deep reasoning, slower but more thorough

### Model Selection Guide

**GPT-4.1:**

- Flagship model for deep reasoning, creativity, and enterprise-grade performance
- Higher cost, ideal for quality
- Best for: Complex analysis, critical business tasks
- Cost: High

**GPT-4.1-mini:**

- Cost-effective, fast GPT-4.1 for high-volume requests
- Best overall value for general use
- Best for: Most day-to-day tasks, high-volume scenarios
- Cost: Medium

**GPT-4o:**

- Multimodal model for text, image, and audio
- Premium performance and strong vision capabilities
- Best for: Image analysis, multimedia tasks
- Cost: High (higher output cost)

**GPT-4o-mini:**

- Ultra-efficient GPT-4o variant
- Low-latency and budget-critical scenarios
- Lowest Azure pricing for "o" models
- Cost: Low

**GPT-5:**

- Next-generation AI with expanded intelligence
- Premium output pricing
- Recommended for advanced enterprise use
- Cost: Very High

**GPT-5-chat:**

- GPT-5 tuned for conversational experiences
- Best for chatbots and dialog agents
- Same cost as standard GPT-5
- Cost: Very High

**GPT-5-mini:**

- Lean, speedy GPT-5 for high-throughput operations
- Cost-sensitive operations
- Lowest GPT-5 price, ideal for efficiency
- Cost: High

**o3:**

- Deep reasoning/coding/vision specialist
- Best for analytic and technical tasks
- Matches gpt-4.1 on cost per token but uses more tokens
- Cost: High (effective)

**o4-mini:**

- Compact, efficient model for math, coding, and real-time tasks
- Half the cost of o3
- Perfect mid-tier choice for reasoning tasks
- Cost: Medium

**Selection Tips:**

- Start with GPT-4.1-mini for most tasks
- Upgrade to GPT-4.1 for critical or complex tasks
- Use o3 or o4-mini for math, logic, coding challenges
- Use GPT-4o/GPT-4o-mini for multimedia tasks
- Consider GPT-5 models for cutting-edge capabilities
- Consider cost vs. quality tradeoffs

---

## Export & Import

> **Export & Import: Scaling Excellence**
> >
> Export/import lets you share proven guides and assistants, foster reuse, and quickly onboard new teams. Always check for missing dependencies and update authentications after import for security.




### Exporting Guides

**When to Export:**

- Share a guide with another team
- Back up your guide configurations
- Version control (commit .zip to Git)
- Document your guide setup
- Transfer guides between environments (dev/staging/prod)

**Export Process:**

1. From dashboard, click **Export** on a guide card
2. Or from editor, click **Export** in header
3. Download the .zip file
4. Store securely (contains configuration details)

**What's Included:**

- Guide metadata (name, description, model)
- Instructions and home page content
- All configuration (temperature, topP, etc.)
- Context options
- Conversation starters
- Custom assistants (full configuration)
- Global assistant references (by name)
- Tool configurations
- Authentication settings (without secrets)
- Files (references, not actual file bytes for large files)

**What's NOT Included:**

- User-specific secrets (API keys, OAuth tokens)
- Team-specific IDs (new IDs generated on import)
- Usage history or analytics

### Importing Guides

**Import Process:**

1. Click **"Import Guide"** button in dashboard header
2. Select a .zip file (previously exported guide)
3. System validates the package structure
4. Preview shows:

- Guide name
- Number of crews
- Number of custom assistants
- Name conflicts (if any)

5. Resolve conflicts if needed:

- Rename conflicting items
- Skip conflicting items
- Overwrite existing items (if permitted)

6. Confirm import
7. System creates:

- New guide with new ID
- New custom assistants with new IDs
- Links to existing global assistants by name
- New crews with new IDs

8. Summary displays:

- Items created
- Items skipped
- Warnings (e.g., missing global assistants)

9. Navigate to the new guide to review and test

**Name Conflict Resolution:**

**Scenario 1: Guide Name Conflict**

- Importing guide named "Research Assistant"
- You already have a guide named "Research Assistant"
- Options:
- Rename import to "Research Assistant (Imported)"
- Rename import to custom name
- Skip import (cancel)

**Scenario 2: Assistant Name Conflict**

- Imported guide includes custom assistant "Python Expert"
- You already have an assistant named "Python Expert"
- Options:
- Create new assistant "Python Expert (Imported)"
- Use your existing "Python Expert" (link instead of create)
- Skip this assistant (guide crew will be incomplete)

**Scenario 3: Missing Global Assistant**

- Imported guide references global assistant "Future Tool v2"
- This global assistant doesn't exist in your environment
- Options:
- Skip this crew member (warning issued)
- Replace with similar global assistant
- Create custom assistant to replace it

**Best Practices for Import:**

- Review the preview carefully before confirming
- Check for missing global assistants (may need to create replacements)
- Rename imported guides to avoid confusion with originals
- Test imported guides thoroughly (some context may be lost)
- Update instructions and context options for your team's needs
- Reconfigure authentication (secrets not imported)

**Post-Import Checklist:**

1. ✅ Review guide name and description
2. ✅ Verify all crew members imported
3. ✅ Check tool configurations
4. ✅ Reconfigure authentication (add API keys, OAuth settings)
5. ✅ Update context options with your values
6. ✅ Test conversation starters
7. ✅ Upload missing files if needed
8. ✅ Run test conversations to verify behavior

---

## Best Practices

> **Best Practices: The Why**
> >
> Clear naming, focused instructions, tool limits, and well-designed context options empower discovery, maintainability, and user experience. Good crew composition streamlines processes and prevents duplication.




### Naming Conventions

**Guides:**

- Use descriptive, role-based names
- Examples:
- ✅ "Market Research Coordinator"
- ✅ "Code Review Lead"
- ✅ "Customer Support Assistant"
- ❌ "Guide 1"
- ❌ "Test"
- ❌ "My Guide"

**Assistants:**

- Use specialization-focused names
- Examples:
- ✅ "Python Code Analyzer"
- ✅ "Security Auditor"
- ✅ "Documentation Writer"
- ❌ "Helper"
- ❌ "Bot"
- ❌ "AI"

**Descriptions:**

- 1-2 sentences
- Explain purpose and key capabilities
- Avoid jargon (unless team-specific)
- Include use case hints

### Instruction Writing Tips

**Be Specific:**
❌ "You are a helpful assistant."
✅ "You are a Python code reviewer. Analyze code for bugs, performance issues, and PEP 8 compliance. Always provide actionable feedback with specific line numbers and code examples."

**Define Scope:**
❌ "Help users with programming."
✅ "You specialize in Python 3.9+ backend development. You do not handle frontend JavaScript, CSS, or HTML. If asked about other languages, politely redirect to appropriate resources."

**Provide Structure:**
❌ "Answer questions about code."
✅ "When analyzing code:

1. First, identify syntax errors
2. Then, check for logical bugs
3. Next, assess performance
4. Finally, verify style compliance

Format responses as:

- Executive Summary (2-3 lines)
- Detailed Findings (categorized)
- Recommendations (prioritized)
- Code Examples (when relevant)"

**Include Examples:**

Example of good feedback:

**Issue**: Line 42 - Potential null pointer exception
**Severity**: High
**Explanation**: `user.profile` can be null if the user hasn't completed onboarding.
**Fix**: Add null check before accessing `user.profile.name`
**Code**:

```python
if user.profile:
    name = user.profile.name
else:
    name = "Unknown User"
```

**Set Tone and Style:**

- Professional: "Provide formal, detailed analysis suitable for executive review."
- Casual: "Be friendly and conversational, like a helpful colleague."
- Educational: "Explain concepts as you go, assuming the user is learning."
- Direct: "Be concise and to-the-point, no unnecessary explanations."

### Security Considerations

**API Keys and Secrets:**

- Never hardcode secrets in instructions
- Use authentication configuration features
- For Service HTTP auth, store keys server-side (not in exported guides)
- Rotate keys regularly

**File Uploads:**

- Only upload files you trust
- Be cautious with user-submitted files
- Review file contents before adding to vector stores
- Consider privacy implications of indexed content

**OAuth Scopes:**

- Request minimum necessary scopes
- Explain to users why each scope is needed
- Document scope requirements in home page

**Data Handling:**

- Instruct guides not to share sensitive information
- Define what constitutes sensitive data for your team
- Include data retention policies in instructions
- Example instruction snippet:

```
  Do not share:
  - Customer credit card numbers
  - Social Security Numbers
  - Passwords or authentication tokens
  - Personal health information
  - Confidential business strategies
  
  If a user requests such information, politely decline and explain the policy.
```

### Troubleshooting

### Common Issues

#### "Name must be unique"

**Problem**: Trying to save a guide or assistant with a name that already exists.
**Solution**: Change the name to something unique within your team.

#### "Failed to load guide"

**Problem**: Guide ID doesn't exist or you don't have permission.
**Solution**: 

- Verify you're a project owner
- Check if guide was deleted
- Try refreshing the page
- Contact admin if issue persists

#### "Avatar upload failed"

**Problem**: Avatar image couldn't be uploaded.
**Solution**:

- Check file format (PNG, JPG, GIF only)
- Verify file size < 5 MB
- Try a different image

#### "OpenAPI spec invalid"

**Problem**: Custom tool upload rejected.
**Solution**:

- Validate OpenAPI spec with online tools
- Ensure version is 3.0 or higher
- Check JSON/YAML syntax
- Verify required fields (servers, paths, operations)

#### "Markdown extraction failed"

**Problem**: File shows "Failed" status after upload.
**Solution**:

- Check error message (hover over status)
- Verify file is not corrupted
- Try re-uploading
- Use alternative format (e.g., TXT instead of PDF)

#### "Guide not delegating to crew"

**Problem**: Guide handles everything itself instead of using crew members.
**Solution**:

- Make guide instructions more explicit about delegation
- Add instruction: "When appropriate, delegate specialized tasks to your crew members."
- Ensure crew member instructions are clear about their expertise
- Verify crew members have relevant tools enabled
- Try adding more specific conversation starters that require crew work

#### "Unsaved changes warning"

**Problem**: Trying to leave editor but changes not saved.
**Solution**:

- Click "Save Guide" or "Save Assistant" before navigating away
- Or confirm you want to discard changes

#### "Import failed"

**Problem**: Can't import a previously exported guide.
**Solution**:

- Verify .zip file is not corrupted
- Check that all required files are in the package
- Resolve name conflicts in preview
- Check for missing global assistants
- Ensure you're importing to correct team
- Try re-exporting and importing again