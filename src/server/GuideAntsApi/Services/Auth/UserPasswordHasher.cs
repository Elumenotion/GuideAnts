using GuideAntsApi.DataModel.Models;
using Microsoft.AspNetCore.Identity;

namespace GuideAntsApi.Services.Auth;

public interface IUserPasswordHasher
{
    string HashPassword(User user, string password);

    bool VerifyPassword(User user, string hashedPassword, string providedPassword);
}

public sealed class UserPasswordHasher : IUserPasswordHasher
{
    private readonly PasswordHasher<User> _passwordHasher = new();

    public string HashPassword(User user, string password)
    {
        return _passwordHasher.HashPassword(user, password);
    }

    public bool VerifyPassword(User user, string hashedPassword, string providedPassword)
    {
        var verification = _passwordHasher.VerifyHashedPassword(user, hashedPassword, providedPassword);
        return verification != PasswordVerificationResult.Failed;
    }
}
