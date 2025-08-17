using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace WpfApp2
{
    public  class RedisClient:IRedisClient
    {
        private static readonly Lazy<ConnectionMultiplexer> _connection;

        static RedisClient()
        {
            _connection = new Lazy<ConnectionMultiplexer>(() =>
                ConnectionMultiplexer.Connect(RedisConfig.ConnectionString)
            );
        }

        private IDatabase Database => _connection.Value.GetDatabase();
        private ISubscriber Subscriber => _connection.Value.GetSubscriber();

        public async Task PublishMessageAsync(string channel, object message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));

            var json = JsonSerializer.Serialize(message);
            await Subscriber.PublishAsync(channel, json).ConfigureAwait(false);
        }

        public async Task SaveDataAsync<T>(string key, T data, TimeSpan? expiry = null)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentNullException(nameof(key));

            var json = JsonSerializer.Serialize(data);
            await Database.StringSetAsync(key, json, expiry).ConfigureAwait(false);
        }

        public void Subscribe(string channel, Action<string> onMessageReceived)
        {
            if (string.IsNullOrEmpty(channel)) throw new ArgumentNullException(nameof(channel));
            if (onMessageReceived == null) throw new ArgumentNullException(nameof(onMessageReceived));

            Subscriber.Subscribe(channel, (_, value) => onMessageReceived(value));
        }

        public void Unsubscribe(string channel)
        {
            if (string.IsNullOrEmpty(channel)) return;
            Subscriber.Unsubscribe(channel);
        }

        public void Dispose()
        {
            if (_connection.IsValueCreated)
                _connection.Value.Dispose();
        }
    }
}
