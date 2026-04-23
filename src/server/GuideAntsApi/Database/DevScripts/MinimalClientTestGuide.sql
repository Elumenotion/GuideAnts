-- MinimalClientTestGuide: create if not exists (team-scoped)
DECLARE @teamId UNIQUEIDENTIFIER = '872d824c-ea3b-41d6-8e9d-9fa1d521d847';
DECLARE @assistantId UNIQUEIDENTIFIER;
SELECT @assistantId = Id FROM dbo.Assistants WHERE Name = 'MinimalClientTestGuide';

IF @assistantId IS NULL
BEGIN
  SET @assistantId = NEWID();
  INSERT INTO dbo.Assistants
  (
    Id, OwnerTeamId, IsGlobal, IsActive, DisplayOrder,
    Name, Description, Instructions, ModelId, Temperature, TopP, ReasoningEffort,
    Kind, Created, Updated
  )
  VALUES
  (
    @assistantId, @teamId, 0, 1, 10,
    'MinimalClientTestGuide',
    'Guide for testing client-handled tools using the minimal client.',
    'When asked to change the page background, call the SetColor tool with a "color" value. Wait for the result and continue the conversation.',
    NULL, NULL, NULL, NULL,
    1, GETUTCDATE(), GETUTCDATE()  -- Kind=Guide (enum value 1)
  );
END
ELSE
BEGIN
  UPDATE dbo.Assistants
  SET OwnerTeamId = @teamId, IsGlobal = 0, IsActive = 1, Updated = GETUTCDATE()
  WHERE Id = @assistantId;
END

-- Upsert OpenAPI schema for client-handled SetColor tool
DECLARE @schemaId UNIQUEIDENTIFIER;
SELECT @schemaId = Id FROM dbo.AssistantOpenApiSchemas WHERE AssistantId = @assistantId AND Name = 'ui';

DECLARE @spec nvarchar(max) =
N'{
  "openapi": "3.0.1",
  "info": { "title": "MinimalClient UI Tools", "version": "v1" },
  "servers": [{ "url": "client://minimal-client" }],
  "paths": {
    "MinimalClient.UI.SetColor": {
      "post": {
        "operationId": "SetColor",
        "summary": "Change the host page background color",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": {
                "type": "object",
                "properties": {
                  "color": { "type": "string", "description": "CSS color, e.g., ''red'' or ''#00ff00''" }
                },
                "required": ["color"],
                "additionalProperties": false
              }
            }
          }
        },
        "responses": { "200": { "description": "OK" } }
      }
    }
  }
}';

IF @schemaId IS NULL
BEGIN
  INSERT INTO dbo.AssistantOpenApiSchemas (Id, AssistantId, Name, ApiHost, SpecificationJson)
  VALUES (NEWID(), @assistantId, 'ui', 'client://minimal-client', @spec);
END
ELSE
BEGIN
  UPDATE dbo.AssistantOpenApiSchemas
  SET ApiHost = 'client://minimal-client', SpecificationJson = @spec, Updated = GETUTCDATE()
  WHERE Id = @schemaId;
END


