using System.ComponentModel.DataAnnotations;

namespace api.Models;

public class AppUser
{
    public int Id { get; set; }

    [MaxLength(100)]
    public string Username { get; set; } = string.Empty;

    [MaxLength(256)]
    public string Password { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.User;

    public bool IsEnabled { get; set; } = true;

    [MaxLength(4000)]
    public string PermissionsJson { get; set; } = "[]";

    public DateTime? PasswordChangedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
