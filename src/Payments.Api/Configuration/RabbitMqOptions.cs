namespace Payments.Api.Configuration;

public class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string Host { get; set; } = "localhost";
    
    private int _port = 5672;
    public int Port
    {
        get => _port;
        set
        {
            if (value < 1 || value > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(Port), value, 
                    "Port must be between 1 and 65535");
            }
            _port = value;
        }
    }
    
    public string VirtualHost { get; set; } = "/";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
