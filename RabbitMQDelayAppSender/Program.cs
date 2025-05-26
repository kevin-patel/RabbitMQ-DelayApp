using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQDelayApp.Shared;
using System.Text;

namespace RabbitMQDelayAppSender
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("RabbitMQ Delay App Sender Started !");

            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
            string? environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

            var config = new ConfigurationBuilder()
                            .SetBasePath(Directory.GetCurrentDirectory())
                            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                            .AddJsonFile($"appsettings.{environment}.json", optional: true)
                            .AddCommandLine(args)
                         .Build();

            using CancellationTokenSource cts = new();

            Console.CancelKeyPress += (s, e) =>
            {
                Console.WriteLine("Canceling the {0} on {1}", nameof(RabbitMQDelayAppSender), DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm"));
                cts.Cancel();
                e.Cancel = true;
            };
            try
            {
                using var host = Host.CreateDefaultBuilder(args)
                            .ConfigureLogging(logging =>
                            {
                                logging.ClearProviders();
                                logging.AddConfiguration(config.GetSection("Logging"));
                                logging.AddConsole();
                                logging.AddDebug();
                            }).ConfigureServices(services =>
                            {
                                RabbitMQConfiguration rabbitMQConfigService = new();
                                config.GetSection(RabbitMQConfiguration.ConfigurationSection)
                                      .Bind(rabbitMQConfigService);
                                services.TryAddSingleton(rabbitMQConfigService);
                            }).Build();

                using var loggerFactory = LoggerFactory.Create(builder =>
                {
                    builder.ClearProviders();
                    builder.AddConfiguration(config.GetSection("Logging"));
                    builder.AddConsole();
                    builder.AddDebug();
                });
                var logger = loggerFactory.CreateLogger<Program>();

                using var scope = host.Services.CreateAsyncScope();
                var rabbitMQConfiguration = scope.ServiceProvider.GetRequiredService<RabbitMQConfiguration>();

            sendProcess:
                await RunAsync(rabbitMQConfiguration, $"RabbitMQ Message Sent on {DateTime.Now:dd-MM-yyyy hh:mm.ss tt}", logger, cts.Token);


                Console.WriteLine("Press 'S' to Send Rabbit MQ Message with Current DateTime");
                var key = Console.ReadKey();
                if (key.Key == ConsoleKey.S)
                {
                    goto sendProcess;
                }

                await host.RunAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to Process {0}, Error: {1}", nameof(RabbitMQDelayAppSender), ex.Message);
                throw;
            }
        }

        private static async Task RunAsync(RabbitMQConfiguration _rabbitMQConfig, string Message, ILogger _logger, CancellationToken cancellationToken = default)
        {
            try
            {
                ConnectionFactory factory;
                if (_rabbitMQConfig.HostName.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                {
                    factory = new ConnectionFactory
                    {
                        HostName = _rabbitMQConfig.HostName,
                        UserName = _rabbitMQConfig.UserName,
                        Password = _rabbitMQConfig.Password,
                    };
                    _logger.LogInformation("Using RabbitMQ Localhost Configuration for Failed Queue");
                }
                else
                {
                    // CloudAMQP URL in format [amqps://User:Pass@kangaroo.rmq.cloudamqp.com/User]
                    string QueueUrl = $"amqp://{_rabbitMQConfig.UserName}:{_rabbitMQConfig.Password}@{_rabbitMQConfig.HostName}/{_rabbitMQConfig.UserName}";
                    factory = new ConnectionFactory
                    {
                        Uri = new Uri(QueueUrl),
                    };
                    _logger.LogInformation("Using RabbitMQ CloudAMQP Configuration for Failed Queue");
                }

                using var connection = await factory.CreateConnectionAsync(cancellationToken);
                using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

                //RabbitMQ FIFO Queue.A TTL should be set (24 hours).
                Random random = new();
                int secondValue = random.Next(10, 60);
                Message = Message + " With Delay of " + secondValue + " Second(s)";
                double dealyTimeInMiliSeconds = TimeSpan.FromSeconds(secondValue).TotalMilliseconds;
                var args = new Dictionary<string, object> { { "x-delayed-type", "direct" } };
                await channel.ExchangeDeclareAsync("AiTripDelayExchange", "x-delayed-message", true, false, args, cancellationToken: cancellationToken);

                // ensure that the queue exists before we publish to it
                await channel.QueueDeclareAsync(queue: _rabbitMQConfig.FailedQueueName, durable: true, exclusive: false, autoDelete: false, arguments: args, cancellationToken: cancellationToken);

                var body = Encoding.UTF8.GetBytes(Message);
                var properties = new BasicProperties
                {
                    Persistent = true,
                    Headers = new Dictionary<string, object?> { { "x-delay", dealyTimeInMiliSeconds } }
                };
                await channel.BasicPublishAsync(exchange: "AiTripDelayExchange", routingKey: string.Empty, mandatory: true, basicProperties: properties, body: body, cancellationToken: cancellationToken);
                Console.WriteLine("Failed Queue Sent message: '{0}'", Message);
                _logger.LogInformation("RabbitMQ Message Sent Successfully for FailedQueue: {FailedQueueName}", _rabbitMQConfig.FailedQueueName);
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to Send the RabbitMQ Message for FailedQueue: {FailedQueueName}", _rabbitMQConfig.FailedQueueName);
                await Task.FromException(ex);
                throw;
            }
        }
    }
}