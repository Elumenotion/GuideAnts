# Connect Your Notion Account to This Guide

Last updated: 2026-08-05

This Guide already has Notion wired up as a tool (via Notion's MCP server) — search, page lookup, and edit
tools are pre-configured. The only thing missing is **your own Notion access token**. Once you add it, the
Guide can act on your Notion workspace.

You don't need to touch any tool configuration, headers, or JSON — that part is already done. This is a
three-step process: get a token from Notion, tell Notion which pages it can see, paste the token into the
Guide.

## Step 1 — Get a Notion access token

1. Go to `notion.so/my-integrations` (or in Notion: **Settings → Connections**) while logged into the Notion
   account/workspace you want the Guide to use.
2. Create a new **Personal Access Token** and make sure the **Notion API** capability is enabled. Creating
   this token also creates a matching **connection** entry in your workspace — you'll use that connection's
   name in Step 2.
3. Copy the token — it starts with `ntn_...`. Keep it somewhere safe for a moment; you'll paste it once and
   won't need to see it again.

## Step 2 — Connect pages to it in Notion

Notion doesn't automatically expose your whole workspace to a new connection — even though the token acts
with your own permissions, each page/database still has to be explicitly connected before the API (and so the
Guide) can see it:

1. Open the page or database in Notion that you want the Guide to be able to read or edit.
2. Click the **•••** menu in the top right corner.
3. Click **Add connections** near the bottom of the menu.
4. Search for and select the connection you created in Step 1.
5. Repeat for every page/database the Guide should have access to. Connecting a top-level page also covers
   its sub-pages.

You can undo this later by opening the same **•••** menu, hovering over the connection's name, and choosing
**Disconnect**.

## Step 3 — Add the token to the Guide

1. Open the Guide in GuideAnts and go to the **Environment** section (in Guide Editor).
2. Find the secret variable the Notion tool is already pointed at (if you're not sure of its name, check the
   **Tools** tab → the Notion tool source → the `Authorization` header will show something like
   `Bearer {{secret:NOTION_TOKEN}}` — that `NOTION_TOKEN` name is the one to look for in Environment).
3. Paste your token from Step 1 as that variable's value.
4. Save. The value is stored as a secret — masked immediately, never shown again in the UI or in logs.

## Step 4 — Confirm it works

Open a chat with the Guide and ask it to do something in Notion, e.g. "Search Notion for our onboarding
doc." If it comes back with real results, you're connected.

If the Guide's Tools tab has a **Test connection** button on the Notion tool source, you can use that
instead/first — it'll tell you immediately if the token is missing or rejected, rather than waiting to see it
fail mid-chat.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Guide says it can't reach Notion / auth error | Token wasn't saved, was pasted with extra whitespace, or was revoked in Notion. |
| Guide can't see a page/database you expect | That page/database hasn't been connected yet — go back to Step 2 and add the connection from that page's **•••** menu. |
| You don't have access to create a token | You need your workspace to allow **Personal Access Tokens** with API capability; ask your Notion workspace admin if this option is missing. |

## Security notes

- Treat this token like a password — anyone using this Guide can act in Notion with whatever pages/databases
  you connected in Step 2.
- If you ever need to revoke access, either disconnect individual pages (Step 2's undo instructions) or
  delete/rotate the token entirely from `notion.so/my-integrations`, then update the secret value in the
  Guide's Environment section.

## Sources

- [Notion Help — Add and manage connections with the API](https://www.notion.com/help/add-and-manage-connections-with-the-api)
- [Notion Developers — Connect to Notion MCP](https://developers.notion.com/guides/mcp/get-started-with-mcp)
