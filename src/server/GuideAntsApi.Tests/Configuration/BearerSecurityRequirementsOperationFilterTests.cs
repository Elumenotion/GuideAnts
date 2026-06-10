using FluentAssertions;
using GuideAntsApi.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace GuideAntsApi.Tests.Configuration;

[TestClass]
public sealed class BearerSecurityRequirementsOperationFilterTests
{
    private static OperationFilterContext CreateContext(params object[] endpointMetadata)
    {
        var apiDescription = new ApiDescription
        {
            ActionDescriptor = new ActionDescriptor
            {
                EndpointMetadata = endpointMetadata.ToList()
            }
        };

        // schemaGenerator/schemaRepository/methodInfo are unused by this filter.
        var methodInfo = typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes)!;
        return new OperationFilterContext(apiDescription, null!, new SchemaRepository(), methodInfo);
    }

    private static OpenApiOperation NewOperation() => new() { Responses = new OpenApiResponses() };

    private static bool HasBearerRequirement(OpenApiOperation operation) =>
        operation.Security != null &&
        operation.Security.Any(req => req.Keys.Any(k => k.Reference?.Id == "Bearer"));

    [TestMethod]
    public void Apply_AddsBearerRequirementAndAuthResponses_ForAuthorizedEndpoint()
    {
        var filter = new BearerSecurityRequirementsOperationFilter();
        var operation = NewOperation();
        var context = CreateContext(new AuthorizeAttribute());

        filter.Apply(operation, context);

        HasBearerRequirement(operation).Should().BeTrue();
        operation.Responses.Should().ContainKey(StatusCodes.Status401Unauthorized.ToString());
        operation.Responses.Should().ContainKey(StatusCodes.Status403Forbidden.ToString());
    }

    [TestMethod]
    public void Apply_DoesNothing_WhenEndpointAllowsAnonymous()
    {
        var filter = new BearerSecurityRequirementsOperationFilter();
        var operation = NewOperation();
        var context = CreateContext(new AuthorizeAttribute(), new AllowAnonymousAttribute());

        filter.Apply(operation, context);

        HasBearerRequirement(operation).Should().BeFalse();
        operation.Responses.Should().BeEmpty();
    }

    [TestMethod]
    public void Apply_DoesNothing_WhenNoMetadataPresent()
    {
        var filter = new BearerSecurityRequirementsOperationFilter();
        var operation = NewOperation();
        var context = CreateContext();

        filter.Apply(operation, context);

        HasBearerRequirement(operation).Should().BeFalse();
        operation.Responses.Should().BeEmpty();
    }

    [TestMethod]
    public void Apply_DoesNothing_WhenNoAuthorizeMetadata()
    {
        var filter = new BearerSecurityRequirementsOperationFilter();
        var operation = NewOperation();
        var context = CreateContext(new object());

        filter.Apply(operation, context);

        HasBearerRequirement(operation).Should().BeFalse();
        operation.Responses.Should().BeEmpty();
    }

    [TestMethod]
    public void Apply_DoesNotDuplicateBearerRequirement_WhenAlreadyPresent()
    {
        var filter = new BearerSecurityRequirementsOperationFilter();
        var operation = NewOperation();
        operation.Security = new List<OpenApiSecurityRequirement>
        {
            new()
            {
                [new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
                }] = Array.Empty<string>()
            }
        };
        var context = CreateContext(new AuthorizeAttribute());

        filter.Apply(operation, context);

        operation.Security.Count(req => req.Keys.Any(k => k.Reference?.Id == "Bearer")).Should().Be(1);
    }

    [TestMethod]
    public void Apply_DoesNotOverwriteExistingResponses()
    {
        var filter = new BearerSecurityRequirementsOperationFilter();
        var operation = NewOperation();
        var existing401 = new OpenApiResponse { Description = "Custom unauthorized" };
        operation.Responses.Add(StatusCodes.Status401Unauthorized.ToString(), existing401);
        var context = CreateContext(new AuthorizeAttribute());

        filter.Apply(operation, context);

        operation.Responses[StatusCodes.Status401Unauthorized.ToString()].Description.Should().Be("Custom unauthorized");
        operation.Responses.Should().ContainKey(StatusCodes.Status403Forbidden.ToString());
    }
}
