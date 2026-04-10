using System.ComponentModel.DataAnnotations;

namespace DotNetSigningServer.Models;

public class ForgotPasswordViewModel
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;
}
