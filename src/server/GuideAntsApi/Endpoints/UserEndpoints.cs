using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models;
using System.Net.Mail;

namespace GuideAntsApi.Endpoints;

public static class UserEndpoints
{
    public static void MapUserEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/users")
            .WithTags("Users")
            .WithOpenApi();

        group.MapGet("/current", async (ApplicationDbContext db) =>
        {
            var user = await GetSingleUserQuery(db)
                .AsNoTracking()
                .FirstOrDefaultAsync();

            if (user == null)
                return Results.NotFound();

            return Results.Ok(new UserDto(user.Id, user.Name, user.Email));
        })
        .WithName("GetCurrentUser")
        .Produces<UserDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

        group.MapPut("/current/personalization", async (UpdateCurrentUserRequest request, ApplicationDbContext db) =>
        {
            var errors = ValidateUpdateCurrentUserRequest(request);
            if (errors.Count > 0)
            {
                return Results.BadRequest(new { errors });
            }

            var user = await GetSingleUserQuery(db).FirstOrDefaultAsync();
            if (user == null)
                return Results.NotFound();

            user.Name = request.Name.Trim();
            user.Email = request.Email.Trim();

            await db.SaveChangesAsync();

            return Results.Ok(new UserDto(user.Id, user.Name, user.Email));
        })
        .WithName("UpdateCurrentUserPersonalization")
        .Produces<UserDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

        // Get user by ID
        group.MapGet("/{userId}", async (Guid userId, ApplicationDbContext db) =>
        {
            var user = await db.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null)
                return Results.NotFound();

            return Results.Ok(new UserDto(user.Id, user.Name, user.Email));
        })
        .WithName("GetUserById")
        .Produces<UserDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);
    }

    private static IOrderedQueryable<DataModel.Models.User> GetSingleUserQuery(ApplicationDbContext db) =>
        db.Users
            .OrderBy(u => u.Created)
            .ThenBy(u => u.Id);

    private static List<string> ValidateUpdateCurrentUserRequest(UpdateCurrentUserRequest request)
    {
        var errors = new List<string>();
        var name = request.Name?.Trim() ?? string.Empty;
        var email = request.Email?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add("Display name is required.");
        }
        else if (name.Length > 255)
        {
            errors.Add("Display name must be 255 characters or fewer.");
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            errors.Add("Email is required.");
        }
        else if (!IsValidEmail(email))
        {
            errors.Add("Email must be a valid email address.");
        }

        return errors;
    }

    private static bool IsValidEmail(string email)
    {
        try
        {
            var address = new MailAddress(email);
            return string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
} 
