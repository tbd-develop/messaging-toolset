﻿using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Confluent.Kafka;
using MessagingToolset.Kafka;
using MessagingToolset.Outbox.Infrastructure.Extensions;
using MessagingToolset.Outbox.Infrastructure.Serialization;
using MessagingToolset.Outbox.Storage;
using MessagingToolset.Topics.Providers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MessagingToolset.Outbox;

public class OutboxService : BackgroundService
{
    private readonly KafkaConfiguration _kafkaConfiguration;
    private readonly IStorageProvider _storageProvider;
    private readonly IKafkaTopicProvider _topicProvider;
    private readonly ILogger<OutboxService> _logger;

    public OutboxService(
        KafkaConfiguration kafkaConfiguration,
        IStorageProvider storageProvider,
        IKafkaTopicProvider topicProvider,
        ILogger<OutboxService> logger)
    {
        _kafkaConfiguration = kafkaConfiguration;
        _storageProvider = storageProvider;
        _topicProvider = topicProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        do
        {
            var message = await _storageProvider.DequeueMessageAsync(stoppingToken);

            if (message is null)
            {
                await Task.Delay(5000, stoppingToken);

                continue;
            }

            if (!await SendMessageAsync(message, stoppingToken))
            {
                await Task.Delay(1000, stoppingToken);

                continue;
            }

            await _storageProvider.Commit(message, stoppingToken);
        } while (!stoppingToken.IsCancellationRequested);
    }

    private async Task<bool> SendMessageAsync(QueuedMessage message, CancellationToken cancellationToken)
    {
        var method = typeof(OutboxService)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(m => m.Name == "ProduceMessageAsync");

        var genericMethod = method.MakeGenericMethod(message.KeyType, message.MessageType);

        var topic = await _topicProvider.GetTopicAsync(message.Topic, cancellationToken);

        var keyAsType = TypeDescriptor.GetConverter(message.KeyType).ConvertFromInvariantString(message.Key);
        var messageAsType = message.Message is not null
            ? JsonSerializer.Deserialize(message.Message, message.MessageType)
            : null;

        if (keyAsType == null) return false;

        await (Task)genericMethod.Invoke(this,
            new object[]
            {
                keyAsType,
                messageAsType,
                topic,
                cancellationToken
            });

        return true;
    }

    private async Task ProduceMessageAsync<TKey, TMessage>(
        TKey key,
        TMessage? message,
        string topic,
        CancellationToken cancellationToken)
        where TMessage : class
    {
        using var producer = new ProducerBuilder<TKey, TMessage>(_kafkaConfiguration.Producers)
            .SetErrorHandler(((current, error) =>
            {
                _logger.LogError("Unable to produce message to Kafka: ({Code}) {Error}", error.Code, error.Reason);
            }))
            .SetKeySerializer(typeof(TKey).GetKeySerializer<TKey>())
            .SetValueSerializer(new EntityValueSerializer<TMessage>())
            .Build();

        await producer.ProduceAsync(
            topic,
            new Message<TKey, TMessage>
            {
                Key = key,
                Value = message,
                Timestamp = Timestamp.Default
            }, cancellationToken);

        producer.Flush(cancellationToken);
    }
}