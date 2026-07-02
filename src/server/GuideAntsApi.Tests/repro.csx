using System.Text;
using AntRunner.ToolCalling.AssistantDefinitions;
using GuideAntsApi.Services.Guides.Skills;

var path = @"D:\repos\GuideAnts\src\client\playwright\fixtures\skills\kanban-video-orchestrator\SKILL.md";
var bytes = File.ReadAllBytes(path);
try {
  var updated = SkillManifestUpdater.ApplyMetadata(bytes, false, 3);
  var text = Encoding.UTF8.GetString(updated);
  SkillFrontmatter.Parse(text);
  Console.WriteLine("OK parse after ApplyMetadata");
} catch (Exception ex) {
  Console.WriteLine("FAIL: " + ex.Message);
}
