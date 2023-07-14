namespace RabbitMQDelayApp.Shared
{
    public class RabbitMQConfiguration
    {
        public const string ConfigurationSection = "RabbitMQConfiguration";

        public string HostName { get; set; }
        public string UserName { get; set; }
        public string Password { get; set; }
        public string QueueName { get; set; }
        public string FailedQueueName { get; set; }
    }
}