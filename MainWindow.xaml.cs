using Microsoft.Extensions.Logging;
using NationalInstruments.DAQmx;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace WpfApp2
{
  

    public partial class MainWindow : Window
    {
        private readonly IDaqProvider _daqProvider;
        private readonly IRedisClient _redisClient;
        private List<ChannelConfig> _channelConfigs;
        private readonly System.Timers.Timer _statusTimer;

        public MainWindow()
        {
            InitializeComponent();
            _daqProvider = new DaqManager();
            _redisClient = new RedisClient();

            // 订阅事件
            _daqProvider.DataAcquired += OnDataAcquired;
            _daqProvider.AcquisitionError += OnAcquisitionError;

            _statusTimer = new System.Timers.Timer(1000);
            _statusTimer.Elapsed += (s, e) => Dispatcher.Invoke(UpdateStatusIndicator);
            _statusTimer.Start();

            ReadChannelConfigs();
            UpdateStatusIndicator();

        }

        private void ReadChannelConfigs()
        {
            try
            {
                string json = File.ReadAllText("channel_config.json");
                var config = JsonConvert.DeserializeObject<Config>(json);
                _channelConfigs = config.Channels;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"读取配置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        private async void OnDataAcquired(List<DaqData> data)
        {
            if (data.Count == 0) return;

            try
            {
                // 发布到Redis
                await _redisClient.PublishMessageAsync(RedisConfig.DataChannel, data);

                // 保存每个通道最新数据
                var latestPerChannel = data.GroupBy(d => d.ChannelID)
                                          .Select(g => g.Last())
                                          .ToList();

                foreach (var item in latestPerChannel)
                {
                    await _redisClient.SaveDataAsync(
                        $"{RedisConfig.DataKeyPrefix}{item.ChannelID}",
                        item);
                }

                // 更新UI
                var now = DateTime.Now;
                if ((now - _lastUiUpdateTime).TotalMilliseconds >= UiUpdateIntervalMs)
                {
                    _lastUiUpdateTime = now;
                    Dispatcher.BeginInvoke(() => // 用BeginInvoke异步更新，避免阻塞
                    {
                        UpdateRedisDisplay(latestPerChannel.LastOrDefault());
                       
                    });
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                MessageBox.Show($"Redis操作失败: {ex.Message}", "错误"));
            }
        }

        private void UpdateRedisDisplay(DaqData data)
        {
            if (data == null) return;

            if (RedisDataDisplay.LineCount >= 50)
                RedisDataDisplay.Clear();

            RedisDataDisplay.AppendText($"[{DateTime.Now:HH:mm:ss}] {data.ChannelID}: {data.Value:F2}\n");
            RedisDataDisplay.ScrollToEnd();
        }
        private DateTime _lastUiUpdateTime = DateTime.MinValue;
        private const int UiUpdateIntervalMs = 200;

       

        private void UpdateStatusIndicator()
        {
            if (StatusIndicator == null) return;

            var status = _daqProvider.IsRunning
                ? AcquisitionStatus.Running
                : AcquisitionStatus.Stopped;

            switch (status)
            {
                case AcquisitionStatus.Stopped:
                    StatusIndicator.Fill = Brushes.Gray;
                    StatusIndicator.ToolTip = "已停止采集";
                    StatusTextBlock.Text = "就绪";
                    break;
                case AcquisitionStatus.Running:
                    StatusIndicator.Fill = Brushes.LimeGreen;
                    StatusIndicator.ToolTip = "正在采集数据";
                    StatusTextBlock.Text = "正在采集...";
                    break;
                case AcquisitionStatus.Error:
                    StatusIndicator.Fill = Brushes.Red;
                    StatusIndicator.ToolTip = "采集错误";
                    StatusTextBlock.Text = "采集错误";
                    break;
            }
        }
        private void OnAcquisitionError(Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                MessageBox.Show($"采集错误: {ex.Message}", "错误");
                StopButton_Click(null, null);
                UpdateStatusIndicator();
            });
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(SampleRateTextBox.Text, out int sampleRate) || sampleRate <= 0)
            {
                MessageBox.Show("请输入有效的采样率");
                return;
            }

            if (!int.TryParse(SamplesPerChannelTextBox.Text, out int samplesPerChannel) || samplesPerChannel <= 0)
            {
                MessageBox.Show("请输入有效的每通道采样数");
                return;
            }

            try
            {
                _daqProvider.Start(sampleRate, samplesPerChannel, _channelConfigs);
                StartButton.IsEnabled = false;
                StopButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"启动失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 停止采集
        /// </summary>
        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _daqProvider.Stop();
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        }

        protected override void OnClosed(EventArgs e)
        {
            // 取消事件订阅
            _daqProvider.DataAcquired -= OnDataAcquired;
            _daqProvider.AcquisitionError -= OnAcquisitionError;

            _daqProvider.Dispose();
            _redisClient.Dispose();
            _statusTimer?.Dispose();
            base.OnClosed(e);
        }



    }
    public class Config
    {
        public List<ChannelConfig> Channels { get; set; }
    }

    public enum AcquisitionStatus
    {
        Stopped,
        Running,
        Error
    }
   
}