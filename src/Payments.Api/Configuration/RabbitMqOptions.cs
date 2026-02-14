using System.ComponentModel.DataAnnotations;

namespace Payments.Api.Configuration;

public class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    [Required]
    public string Host { get; set; } = "localhost";
    
    [Range(1, 65535)]
    public int Port { get; set; } = 5672;
    
    [Required]
    public string VirtualHost { get; set; } = "/";
    
    [Required]
    public string Username { get; set; } = string.Empty;
    
    [Required]
    public string Password { get; set; } = string.Empty;
}
