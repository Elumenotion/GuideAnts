using System.Text;
using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.Guides.Skills;

public sealed class SkillImportService : ISkillImportService
{
    private readonly SkillPackageParser _parser = new();

    public ParsedSkillPackage ParseFolderEntries(
        IReadOnlyDictionary<string, byte[]> entries,
        string source = "Imported") =>
        _parser.ParseFolderEntries(entries, source);

    public ParsedSkillPackage ParseZip(Stream zipStream, string source = "Imported") =>
        _parser.ParseZip(zipStream, source);

    public SkillPrerequisiteMappingResult MapPrerequisites(ParsedSkillPackage package) =>
        SkillPrerequisiteMapper.Map(package.Frontmatter);

    public FileUploadDto CreateCodeInterpreterPlaceholder(string skillName)
    {
        var readme = Encoding.UTF8.GetBytes(
            $"# Sandbox placeholder for skill '{skillName}'\n\n" +
            "Created by create-assistant-from-skill to enable code_interpreter capability.\n");
        return new FileUploadDto(
            "CodeInterpreter",
            null,
            $"skills-{skillName}-sandbox-placeholder.txt",
            readme,
            "text/plain");
    }
}
