using NationalInstruments.DAQmx;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WpfApp2
{
    public  class DaqManager:IDaqProvider
    {
        private const int ChannelCount = 16;
        private NationalInstruments.DAQmx.Task _analogInputTask;
        private AnalogMultiChannelReader _reader;
        private bool _isRunning;
        private IEnumerable<ChannelConfig> _channelConfigs;
        private int _samplesPerChannel;

        public bool IsRunning => _isRunning;
        public event Action<List<DaqData>> DataAcquired;
        public event Action<Exception> AcquisitionError;

        public void Start(int sampleRate, int samplesPerChannel, IEnumerable<ChannelConfig> channelConfigs)
        {
            if (_isRunning) throw new InvalidOperationException("采集已在运行中");

            _channelConfigs = channelConfigs ?? throw new ArgumentNullException(nameof(channelConfigs));
            _samplesPerChannel = samplesPerChannel;
            _isRunning = true;

            try
            {
                CreateAnalogInputTask(sampleRate, samplesPerChannel);
                BeginReadData();
            }
            catch (Exception ex)
            {
                _isRunning = false;
                AcquisitionError?.Invoke(ex);
            }
        }

        public void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;
            CleanupTask();
        }

        private void CreateAnalogInputTask(int sampleRate, int samplesPerChannel)
        {
            // 检查设备是否存在
            var devices = DaqSystem.Local.Devices;
            if (devices == null)
            {
                throw new Exception("未找到DAQ设备，请检查硬件连接");
            }

            _analogInputTask?.Dispose();
            _analogInputTask = new NationalInstruments.DAQmx.Task("AI Task");

            // 创建通道（可配置设备名称，避免硬编码）
            for (int i = 0; i < ChannelCount; i++)
            {
                _analogInputTask.AIChannels.CreateVoltageChannel(
                    $"Dev1/ai{i}",
                    $"Channel{i}",
                    AITerminalConfiguration.Nrse,
                    0.0, 10.0,
                    AIVoltageUnits.Volts);
            }

            // 配置采样时钟
            _analogInputTask.Timing.ConfigureSampleClock(
                "",
                sampleRate,
                SampleClockActiveEdge.Rising,
                SampleQuantityMode.ContinuousSamples,
                samplesPerChannel);

            _reader = new AnalogMultiChannelReader(_analogInputTask.Stream);
            _reader.SynchronizeCallbacks = true;
        }

        private void BeginReadData()
        {
            if (!_isRunning || _analogInputTask == null) return;

            try
            {
                _reader.BeginReadMultiSample(_samplesPerChannel, OnReadComplete, null);
            }
            catch (Exception ex)
            {
                AcquisitionError?.Invoke(ex);
                Stop();
            }
        }

        private void OnReadComplete(IAsyncResult ar)
        {
            if (!_isRunning) return;

            try
            {
                var rawData = _reader.EndReadMultiSample(ar);
                var processedData = ProcessData(rawData);
                DataAcquired?.Invoke(processedData);
                BeginReadData(); // 继续读取
            }
            catch (Exception ex)
            {
                AcquisitionError?.Invoke(ex);
                Stop();
            }
        }

        /// <summary>
        /// 数据转换处理（应用斜率和截距）
        /// </summary>
        private List<DaqData> ProcessData(double[,] rawData)
        {
            var result = new List<DaqData>();
            int sampleCount = rawData.GetLength(1);
            DateTime batchTime = DateTime.Now;

            for (int channel = 0; channel < ChannelCount; channel++)
            {
                var config = _channelConfigs.FirstOrDefault(c => c.Channel == channel);
                if (config == null) continue;

                string channelId = $"Channel{channel}";
                for (int sample = 0; sample < sampleCount; sample++)
                {
                    result.Add(new DaqData
                    {
                        ChannelID = channelId,
                        Value = config.Slope * rawData[channel, sample] + config.Intercept,
                        Timestamp = batchTime,
                        Status = "Valid"
                    });
                }
            }

            return result;
        }

        private void CleanupTask()
        {
            if (_analogInputTask != null)
            {
                if (!_analogInputTask.IsDone)
                    _analogInputTask.Stop();
                _analogInputTask.Dispose();
                _analogInputTask = null;
            }
            _reader = null;
        }

        public void Dispose()
        {
            Stop();
            CleanupTask();
        }
    }
}
