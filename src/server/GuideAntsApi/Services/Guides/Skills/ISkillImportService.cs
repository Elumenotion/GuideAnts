using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.Guides.Skills;

public interface ISkillImportService
{
    ParsedSkillPackage ParseFolderEntries(
        IReadOnlyDictionary<string, byte[]> entries,
        string source = "Imported");

    ParsedSkillPackage ParseZip(Stream zipStream, string source = "Imported");

    SkillPrerequisiteMappingResult MapPrerequisites(ParsedSkillPackage package);

    FileUploadDto CreateCodeInterpreterPlaceholder(string skillName);
}
