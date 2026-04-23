using FluentAssertions;
using GuideAntsApi.Services;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public class SlugGeneratorTests
{
    [TestMethod]
    public void Generate_ProducesAsciiOnlySlug_ForQuotedInput()
    {
        var slug = SlugGenerator.Generate("Doug's \"Notebook\"");

        slug.Should().Be("doug-x27xs-x22xnotebook-x22x");
        slug.Should().MatchRegex("^[a-z0-9-]+$");
    }

    [TestMethod]
    public void Generate_TransliteratesDiacritics_ToAscii()
    {
        var slug = SlugGenerator.Generate("Café résumé");

        slug.Should().Be("cafe-resume");
    }

    [TestMethod]
    public void DecodeEncodedSegments_DecodesEscapedCodePoints()
    {
        var decoded = SlugGenerator.DecodeEncodedSegments("a-x27x-b-x22x-c");

        decoded.Should().Be("a-'-b-\"-c");
    }

    [TestMethod]
    public void IsFilesystemSafeSlug_FlagsUnsafePatterns()
    {
        SlugGenerator.IsFilesystemSafeSlug("safe-slug-1").Should().BeTrue();
        SlugGenerator.IsFilesystemSafeSlug("contains'quote").Should().BeFalse();
        SlugGenerator.IsFilesystemSafeSlug("double--dash").Should().BeFalse();
    }
}
