﻿using System.Text;
using Confluent.Kafka;

namespace MessagingToolset.Outbox.Infrastructure.Serialization;

public class StringKeySerializer : ISerializer<string>
{
    public byte[] Serialize(string data, SerializationContext context)
    {
        return Encoding.UTF8.GetBytes(data);
    }
}