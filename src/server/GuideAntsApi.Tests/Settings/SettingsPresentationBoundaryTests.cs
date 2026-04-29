using FluentAssertions;
using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.Tests.Settings;

[TestClass]
public sealed class SettingsPresentationBoundaryTests
{
    [TestMethod]
    public void SettingsMetadataDtos_DoNotExposePresentationFields()
    {
        var forbiddenByType = new Dictionary<Type, string[]>
        {
            [typeof(SettingsSectionSummaryDto)] = ["DisplayName", "DisplayOrder"],
            [typeof(SettingsSectionSchemaDto)] = ["DisplayName", "DisplayOrder"],
            [typeof(SettingsServiceDefinitionDto)] = ["DisplayName"],
            [typeof(SettingsProviderDefinitionDto)] = ["DisplayName", "MarketingSummary"],
            [typeof(SettingsDependencyFieldDto)] = ["DisplayName"],
            [typeof(SettingsRuntimeKeyRequirementDto)] = ["DisplayName", "ChangeHint"],
            [typeof(ProviderFieldMetadataDto)] = ["Label", "HelpText"],
            [typeof(RuntimeKeyDto)] = ["DisplayName", "ChangeHint"],
            [typeof(ProviderEditorStateDto)] = ["DisplayName"],
            [typeof(ServiceEditorStateDto)] = ["DisplayName"],
            [typeof(SettingsRuntimeDependencyDto)] = ["DisplayName", "ChangeHint"],
            [typeof(SettingsServiceReadinessDto)] = ["DisplayName"],
        };

        foreach (var (dtoType, forbiddenProps) in forbiddenByType)
        {
            var propNames = dtoType.GetProperties()
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);

            propNames.Should().NotIntersectWith(
                forbiddenProps,
                $"{dtoType.Name} must remain semantic-only and cannot reintroduce presentation fields.");
        }
    }
}
