using Microsoft.Extensions.FileProviders;

namespace GuideAntsApi.Configuration;

public static class UiApplicationBuilderExtensions
{
    public static WebApplication UseGuideAntsUiPipeline(this WebApplication app, IConfiguration configuration)
    {
        var uiRootPath = configuration["Ui:RootPath"]!;
        var resolvedUiRootPath = Path.IsPathRooted(uiRootPath)
            ? uiRootPath
            : Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, uiRootPath));

        var uiDevServerUrl = configuration["Ui:DevServerUrl"];
        if (app.Environment.IsDevelopment() && !string.IsNullOrWhiteSpace(uiDevServerUrl))
        {
            app.MapWhen(
                context => IsUiRequest(context),
                spaApp =>
                {
                    spaApp.UseSpa(spa =>
                    {
                        spa.Options.SourcePath = Path.GetFullPath(
                            Path.Combine(app.Environment.ContentRootPath, "..", "..", "client"));
                        spa.UseProxyToSpaDevelopmentServer(uiDevServerUrl);
                    });
                });

            return app;
        }

        Directory.CreateDirectory(resolvedUiRootPath);
        var uiFileProvider = new PhysicalFileProvider(resolvedUiRootPath);

        app.UseWhen(
            context => IsUiRequest(context),
            uiApp =>
            {
                uiApp.UseDefaultFiles(new DefaultFilesOptions { FileProvider = uiFileProvider });
                uiApp.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = uiFileProvider,
                    OnPrepareResponse = staticFileContext =>
                    {
                        var requestPath = staticFileContext.Context.Request.Path.Value ?? string.Empty;
                        if (requestPath.Contains("/assets/", StringComparison.OrdinalIgnoreCase))
                        {
                            staticFileContext.Context.Response.Headers["Cache-Control"] =
                                "public, max-age=31536000, immutable";
                            return;
                        }

                        staticFileContext.Context.Response.Headers["Cache-Control"] = "public, max-age=3600";
                    }
                });
            });

        app.MapFallback(async context =>
        {
            if (!IsUiRequest(context))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var indexFile = uiFileProvider.GetFileInfo("index.html");
            if (!indexFile.Exists)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers["Cache-Control"] = "no-cache";
            await context.Response.SendFileAsync(indexFile);
        });

        return app;
    }

    private static bool IsUiRequest(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            return false;
        }

        return IsUiPath(context.Request.Path);
    }

    private static bool IsUiPath(PathString path)
    {
        return !path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWithSegments("/chat", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWithSegments("/sandbox", StringComparison.OrdinalIgnoreCase);
    }
}
