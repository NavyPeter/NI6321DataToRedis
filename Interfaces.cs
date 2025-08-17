using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WpfApp2
{
    /// <summary>
    /// 数据采集提供者接口
    /// </summary>
    public interface IDaqProvider : IDisposable
    {
        event Action<List<DaqData>> DataAcquired; // 数据采集完成事件
        event Action<Exception> AcquisitionError; // 采集错误事件

        bool IsRunning { get; }
        void Start(int sampleRate, int samplesPerChannel, IEnumerable<ChannelConfig> channelConfigs);
        void Stop();
    }

    /// <summary>
    /// Redis操作接口
    /// </summary>
    public interface IRedisClient : IDisposable
    {
        Task PublishMessageAsync(string channel, object message);
        Task SaveDataAsync<T>(string key, T data, TimeSpan? expiry = null);
        void Subscribe(string channel, Action<string> onMessageReceived);
        void Unsubscribe(string channel);
    }
}
