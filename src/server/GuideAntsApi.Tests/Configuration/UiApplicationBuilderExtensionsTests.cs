using System.Reflection;
using FluentAssertions;
using GuideAntsApi.Configuration;
using Microsoft.AspNetCore.Http;

namespace GuideAntsApi.Tests.Configuration;

[TestClass]
public class UiApplicationBuilderExtensionsTests
{
    [TestMethod]
    [DataRow("/api", false)]
    [DataRow("/api/projects", false)]
    [DataRow("/swagger", false)]
    [DataRow("/chat", false)]
    [DataRow("/sandbox", false)]
    [DataRow("/projects/demo", true)]
    [DataRow("/", true)]
    public void IsUiPath_Excludes_BackendPrefixes(string path, bool expected)
    {
        var method = typeof(UiApplicationBuilderExtensions)
            .GetMethod("IsUiPath", BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();
        var actual = (bool)method!.Invoke(null, new object[] { new PathString(path) })!;

        actual.Should().Be(expected);
    }

    [TestMethod]
    [DataRow("GET", "/projects/demo", true)]
    [DataRow("HEAD", "/projects/demo", true)]
    [DataRow("POST", "/projects/demo", false)]
    [DataRow("GET", "/api/projects", false)]
    [DataRow("HEAD", "/swagger/index.html", false)]
    public void IsUiRequest_RequiresGetOrHead_AndUiPath(string method, string path, bool expected)
    {
        var isUiRequestMethod = typeof(UiApplicationBuilderExtensions)
            .GetMethod("IsUiRequest", BindingFlags.NonPublic | BindingFlags.Static);

        isUiRequestMethod.Should().NotBeNull();

        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        var actual = (bool)isUiRequestMethod!.Invoke(null, new object[] { context })!;

        actual.Should().Be(expected);
    }
}
