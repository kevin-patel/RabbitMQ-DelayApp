using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQDelayApp.Shared;
using System.Text;

namespace RabbitMQDelayAppReciever;

internal class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("RabbitMQ Delay App Reciever Started !");

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
            Console.WriteLine("Canceling the RabbitMQDelayAppReciever on {0}", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm"));
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
            await Task.Delay(TimeSpan.FromSeconds(1));
            await RunAsync(rabbitMQConfiguration, logger, cts.Token);
            await host.RunAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to Process RabbitMQDelayAppReciever, Error: {ex}");
            throw;
        }
    }

    private static async Task RunAsync(RabbitMQConfiguration _rabbitMQConfig, ILogger _logger, CancellationToken cancellationToken = default)
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

            using var connection = factory.CreateConnection();
            using var channel = connection.CreateModel();

            // ensure that the queue exists before we publish to it
            channel.QueueBind(queue: _rabbitMQConfig.FailedQueueName, "AiTripDelayExchange", string.Empty, null);

            Console.WriteLine(" [*] Waiting for messages in Failed Queue.");
            int messageCount = Convert.ToInt16(channel.MessageCount(_rabbitMQConfig.FailedQueueName));
            Console.WriteLine(" Listening to the Failed queue. This channels has {0} messages on the queue", messageCount);

            var consumer = new EventingBasicConsumer(channel);

            // add the message receive event
            await Task.Run(() =>
              {
                  consumer.Received += (model, deliveryEventArgs) =>
                  {
                      var body = deliveryEventArgs.Body.ToArray();
                      // convert the message back from byte[] to a string
                      var message = Encoding.UTF8.GetString(body);
                      Console.WriteLine("Failed Queue Received message: '{0}' on {1}", message, DateTime.Now.ToString("dd-MM-yyyy hh:mm.ss tt"));
                      if (message is null)
                      {
                          throw new ArgumentNullException("Recieved Empty Message from RabbitMQ Failed Queue");
                      }
                      //var IsMessagePosted = await _webHookClient.SendWebhookRequestAsync(WebhookRequest.RequestUrl, WebhookRequest.RequestBody, WebhookRequest.MessageTimeoutInSeconds, cancellationToken);
                      //// ack the message, ie. confirm that we have processed it otherwise it will be requeued a bit later
                      channel.BasicAck(deliveryEventArgs.DeliveryTag, false);
                      //if (!IsMessagePosted)
                      //{
                      //    await RabbitMQClient.SendToFailedRabbitMQAsync(_rabbitMQConfig, WebhookRequest, _logger);
                      //}
                  };
              }, cancellationToken);
            // start consuming & autoAck the message, ie. confirm that we have processed it otherwise it will be requeued a bit later
            _ = channel.BasicConsume(queue: _rabbitMQConfig.FailedQueueName, autoAck: false, consumer: consumer);
            Console.ReadLine();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to Send the RabbitMQ Message for FailedQueue: {FailedQueueName}", _rabbitMQConfig.FailedQueueName);
            await Task.FromException(ex);
            //throw;
        }
    }
}