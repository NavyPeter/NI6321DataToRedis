using System.Text.Json;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WpfApp2
{
    public class RedisManager
    {
        private static readonly Lazy<ConnectionMultiplexer> _connection;

        static RedisManager()
        {
            _connection = new Lazy<ConnectionMultiplexer>(() =>
                ConnectionMultiplexer.Connect(RedisConfig.ConnectionString)
            );
        }

        /// <summary>
        /// 获取数据库连接
        /// </summary>
        /// <returns></returns>
        public IDatabase GetDatabase() => _connection.Value.GetDatabase();

        /// <summary>
        /// 获取发布订阅实例
        /// </summary>
        /// <returns></returns>
        public ISubscriber GetSubscriber() => _connection.Value.GetSubscriber();

        /// <summary>
        /// 存储数据到Redis
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="key"></param>
        /// <param name="data"></param>
        /// <param name="expiry"></param>
        public void SaveData<T>(string key, T data, TimeSpan? expiry = null)
        {
            var json = JsonSerializer.Serialize(data);
            GetDatabase().StringSet(key, json, expiry);
        }

        /// <summary>
        /// 从Redis读取数据
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="key"></param>
        /// <returns></returns>
        public T GetData<T>(string key)
        {
            var json = GetDatabase().StringGet(key);
            if (json.IsNullOrEmpty)
                return default;

            return JsonSerializer.Deserialize<T>(json);
        }

        /// <summary>
        /// 发布消息
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="message"></param>
        public void PublishMessage(string channel, object message)
        {
            var json = JsonSerializer.Serialize(message);
            GetSubscriber().Publish(channel, json);
        }

        /// <summary>
        /// 订阅消息
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="onMessageReceived"></param>
        public void Subscribe(string channel, Action<string> onMessageReceived)
        {
            GetSubscriber().Subscribe(channel, (_, value) =>
            {
                onMessageReceived(value);
            });
        }

        // 添加取消订阅方法
        public void Unsubscribe(string channel)
        {
            GetSubscriber().Unsubscribe(channel);
        }

        // 重载：支持取消特定的订阅处理
        public void Unsubscribe(string channel, Action<RedisChannel, string> onMessageReceived)
        {
            GetSubscriber().Unsubscribe(channel, (channel, value) =>
            {
                onMessageReceived(channel, value);
            });
        }

        /// <summary>
        /// 关闭连接
        /// </summary>
        public void Dispose()
        {
            if (_connection.IsValueCreated)
            {
                _connection.Value.Dispose();
            }
        }
    }
}