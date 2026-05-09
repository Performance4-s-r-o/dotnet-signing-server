using System.ComponentModel.DataAnnotations;

namespace DotNetSigningServer.Models;

public class ChangePasswordViewModel
{
    [Required]
    [DataType(DataType.Password)]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    [DataType(DataType.Password)]
    public string NewPassword { get; set; } = string.Empty;

    [Required]
    [Compare(nameof(NewPassword))]
    [DataType(DataType.Password)]
    public string ConfirmPassword { get; set; } = string.Empty;
}
