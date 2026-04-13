using Microsoft.Extensions.Configuration;

namespace api.Security;

public static class BootstrapAdminPasswordResolver
{
    public const string ConfigKey = "BootstrapAdmin:Password";

    public static string GetRequiredPassword(IConfiguration configuration)
    {
        var password = configuration[ConfigKey];
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                $"The database is empty and requires an initial admin password. Set {ConfigKey} (or env var BootstrapAdmin__Password) before starting the API.");
        }

        if (password.Length < 8)
        {
            throw new InvalidOperationException("Bootstrap admin password must be at least 8 characters.");
        }

        return password;
    }
}
