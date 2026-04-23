using System.Text;
using System.IO;
using System.Linq;

namespace AntRunner.ToolCalling.Functions
{
    /// <summary>
    /// Generates an OpenAPI schema that exposes each crew member assistant
    /// as a local tool operation routed through Agent.Invoke.
    /// </summary>
    public static class CrewBridgeSchemaGenerator
    {
        public static string GetSchema(IEnumerable<AssistantDefinition> crewAssistants)
        {
            if (crewAssistants == null) throw new ArgumentNullException(nameof(crewAssistants));
            var assistants = crewAssistants
                .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                .GroupBy(a => a.Name!, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            if (assistants.Count == 0) throw new ArgumentException("Crew list is empty", nameof(crewAssistants));

            // Build minimal OpenAPI object
            var ms = new MemoryStream();
            using var doc = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false });
            doc.WriteStartObject();
            doc.WriteString("openapi", "3.0.0");

            // info
            doc.WritePropertyName("info");
            doc.WriteStartObject();
            doc.WriteString("title", "Crew Bridge Tools");
            doc.WriteString("version", "v1");
            doc.WriteEndObject();

            // servers
            doc.WritePropertyName("servers");
            doc.WriteStartArray();
            doc.WriteStartObject();
            doc.WriteString("url", "tool://localhost");
            doc.WriteString("description", "Local dispatcher for assistant-to-assistant calls");
            doc.WriteEndObject();
            doc.WriteEndArray();

            // paths
            doc.WritePropertyName("paths");
            doc.WriteStartObject();

            // Single host path for local dispatcher
            doc.WritePropertyName("AntRunner.Chat.Agent.Invoke");
            doc.WriteStartObject();

            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assistant in assistants)
            {
                var name = assistant.Name!;
                var operationId = SanitizeOperationId(name, usedIds); // must match ^[a-zA-Z0-9_-]{1,64}$
                var opKey = operationId; // use same as method key for clarity

                doc.WritePropertyName(opKey);
                doc.WriteStartObject();
                doc.WriteString("operationId", operationId);
                var summary = !string.IsNullOrWhiteSpace(assistant.Description)
                    ? assistant.Description
                    : $"Invoke assistant '{name}' through Agent.Invoke";
                doc.WriteString("summary", summary);

                // requestBody
                doc.WritePropertyName("requestBody");
                doc.WriteStartObject();
                doc.WriteBoolean("required", true);
                doc.WritePropertyName("content");
                doc.WriteStartObject();
                doc.WritePropertyName("application/json");
                doc.WriteStartObject();
                doc.WritePropertyName("schema");
                doc.WriteStartObject();
                doc.WriteString("type", "object");

                // properties
                doc.WritePropertyName("properties");
                doc.WriteStartObject();
                // assistantName (default=target)
                doc.WritePropertyName("assistantName");
                doc.WriteStartObject();
                doc.WriteString("type", "string");
                doc.WriteString("default", name);
                doc.WriteEndObject();
                // instructions
                doc.WritePropertyName("instructions");
                doc.WriteStartObject();
                doc.WriteString("type", "string");
                doc.WriteEndObject();
                doc.WriteEndObject(); // properties

                // required
                doc.WritePropertyName("required");
                doc.WriteStartArray();
                doc.WriteStringValue("assistantName");
                doc.WriteStringValue("instructions");
                doc.WriteEndArray();

                doc.WriteBoolean("additionalProperties", false);
                doc.WriteEndObject(); // schema
                doc.WriteEndObject(); // application/json
                doc.WriteEndObject(); // content
                doc.WriteEndObject(); // requestBody

                doc.WriteEndObject(); // operation
            }

            doc.WriteEndObject(); // path object
            doc.WriteEndObject(); // paths

            doc.WriteEndObject(); // root
            doc.Flush();
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static string SanitizeKey(string name)
        {
            var sb = new StringBuilder(name.Length + 8);
            foreach (var ch in name)
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
                else sb.Append('_');
            }
            var key = sb.ToString();
            if (string.IsNullOrWhiteSpace(key)) key = "op";
            return key;
        }

        private static string SanitizeOperationId(string name, HashSet<string> used)
        {
            // allow only [a-zA-Z0-9_-], max 64 chars
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
            {
                if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-') sb.Append(ch);
                else sb.Append('_');
            }
            var baseId = sb.ToString().Trim('_', '-');
            if (string.IsNullOrEmpty(baseId)) baseId = "op";
            if (baseId.Length > 64) baseId = baseId.Substring(0, 64);

            var candidate = baseId;
            var i = 2;
            while (used.Contains(candidate))
            {
                var suffix = "_" + i.ToString();
                var limit = 64 - suffix.Length;
                candidate = (baseId.Length > limit ? baseId.Substring(0, limit) : baseId) + suffix;
                i++;
            }
            used.Add(candidate);
            return candidate;
        }
    }
}


