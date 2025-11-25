using System.ComponentModel.DataAnnotations;

namespace DotNetSigningServer.Models;

public class PricingPlan
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [Range(0, double.MaxValue)]
    public decimal PricePer100 { get; set; }

    public bool IsActive { get; set; } = true;
}
