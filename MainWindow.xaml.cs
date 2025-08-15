using NationalInstruments.DAQmx;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
using Microsoft.Extensions.Logging;

namespace WpfApp2
{
    /// <summary>
    /// 配置文件模型
    /// </summary>
    public class Config
    {
        public List<ChannelConfig> Channels { get; set; }
    }

    public enum AcquisitionStatus
    {
        Stopped,    // 已停止
        Running,    // 正在运行
        Error       // 发生错误
    }

    public partial class MainWindow : Window
    {
        //常量定义
        private const int ChannelCount = 16;

        // 界面相关变量
        private bool isAcquiring = false;

        private double yScale = 100;
        private AcquisitionStatus currentStatus = AcquisitionStatus.Stopped;
        private System.Timers.Timer statusTimer;

        // 数据采集相关变量
        private NationalInstruments.DAQmx.Task analogInputTask;

        private AnalogMultiChannelReader reader;
        private double[,] latestData;

        private List<List<Line>> channelLines = new List<List<Line>>();

        private List<ChannelConfig> channelConfigs;
        private RedisManager redisManager = new RedisManager();

        public MainWindow()
        {
            InitializeComponent();
            statusTimer = new System.Timers.Timer(1000);
            statusTimer.Elapsed += StatusTimer_Elapsed;
            statusTimer.Start();
            for (int i = 0; i < ChannelCount; i++)
            {
                channelLines.Add(new List<Line>());
            }

            ReadChannelConfigs();

            UpdateStatusIndicator();
        }

        /// <summary>
        /// 读取json配置文件
        /// </summary>
        private void ReadChannelConfigs()
        {
            try
            {
                string configFilePath = "channel_config.json";
                string json = File.ReadAllText(configFilePath);
                var config = JsonConvert.DeserializeObject<Config>(json);
                channelConfigs = config.Channels;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"读取配置文件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 更新状态显示
        /// </summary>
        private void UpdateStatusIndicator()
        {
            if (StatusIndicator == null) return;

            StatusIndicator.Dispatcher.Invoke(() =>
            {
                switch (currentStatus)
                {
                    case AcquisitionStatus.Stopped:
                        StatusIndicator.Fill = new SolidColorBrush(Colors.Gray);
                        StatusIndicator.ToolTip = "已停止采集";
                        break;

                    case AcquisitionStatus.Running:
                        StatusIndicator.Fill = new SolidColorBrush(Colors.LimeGreen);
                        StatusIndicator.ToolTip = "正在采集数据";
                        break;

                    case AcquisitionStatus.Error:
                        StatusIndicator.Fill = new SolidColorBrush(Colors.Red);
                        StatusIndicator.ToolTip = "采集过程中发生错误";
                        break;
                }
            });
        }

        /// <summary>
        /// 状态定时器回调
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void StatusTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (isAcquiring)
                {
                    if (analogInputTask == null)
                    {
                        MessageBox.Show("数据采集任务异常，请检查！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        StopButton_Click(null, null);
                        currentStatus = AcquisitionStatus.Error;
                        UpdateStatusIndicator();
                    }
                    else
                    {
                        currentStatus = AcquisitionStatus.Running;
                        UpdateStatusIndicator();
                    }
                }
                else
                {
                    currentStatus = AcquisitionStatus.Stopped;
                    UpdateStatusIndicator();
                }
            }));
        }

        /// <summary>
        /// 读取数据完成回调
        /// </summary>
        private void OnReadComplete(IAsyncResult ar)
        {
            if (!isAcquiring) return;
            latestData = reader.EndReadMultiSample(ar);
            int sampleCount = latestData.GetLength(1);
            int totalItems = ChannelCount * sampleCount;
            var dataToPublish = new List<DaqData>(totalItems);
            try
            {
                latestData = reader.EndReadMultiSample(ar);
                var processedData = new double[ChannelCount, sampleCount];
                string[] channelIDS = new string[ChannelCount];
                for (int i = 0; i < ChannelCount; i++)
                {
                    channelIDS[i] = $"Channel{i}";
                }
                DateTime batchtime = DateTime.Now;
                for (int i = 0; i < ChannelCount; i++)
                {
                    var config = channelConfigs.FirstOrDefault(c => c.Channel == i);
                    string channelID = channelIDS[i];
                    if (config != null)
                    {
                        for (int s = 0; s < latestData.GetLength(1); s++)
                        {
                            processedData[i, s] = config.Slope * latestData[i, s] + config.Intercept;//这里乘以系数+截距
                            // 生成要发布的数据对象
                            dataToPublish.Add(new DaqData
                            {
                                ChannelID = channelID,
                                Value = processedData[i, s],
                                Timestamp = batchtime,
                                Status = "Valid"
                            });
                        }
                    }
                }

                latestData = processedData; // 更新为处理后的数据
                Dispatcher.BeginInvoke(new Action(() => UpdateWaveformDisplay()));

                if (dataToPublish.Count > 0)
                {
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            // 发布批量数据
                            redisManager.PublishMessage(RedisConfig.DataChannel, dataToPublish);
                            // 同时存储每个通道的最新数据到Redis
                            foreach (var data in dataToPublish.GroupBy(d => d.ChannelID).Select(g => g.Last()))
                            {
                                string key = $"{RedisConfig.DataKeyPrefix}{data.ChannelID}";
                                redisManager.SaveData(key, data);
                                if (RedisDataDisplay.LineCount >= 50)
                                {
                                    RedisDataDisplay.Clear();
                                }
                                RedisDataDisplay.AppendText($"[{DateTime.Now:f}] {data.Value}\n");
                                RedisDataDisplay.ScrollToEnd();
                            }
                        }
                        catch (Exception ex)
                        {
                            // 捕获并记录发布异常，避免影响主采集线程
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                MessageBox.Show($"Redis发布数据失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                            }));
                        }
                    });
                }

                reader.BeginReadMultiSample(latestData.GetLength(1), OnReadComplete, null);

                currentStatus = AcquisitionStatus.Running;
                UpdateStatusIndicator();
            }
            catch (DaqException ex)
            {
                if (isAcquiring)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        MessageBox.Show($"采集过程中发生错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        StopButton_Click(null, null);
                        currentStatus = AcquisitionStatus.Error;
                        UpdateStatusIndicator();
                    }));
                }
            }
            catch (Exception ex)
            {
                if (isAcquiring)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        MessageBox.Show($"发生未知错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        StopButton_Click(null, null);
                        currentStatus = AcquisitionStatus.Error;
                        UpdateStatusIndicator();
                    }));
                }
            }
        }

        /// <summary>
        /// 开始采集按钮点击事件
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!int.TryParse(SampleRateTextBox.Text, out int sampleRate) || sampleRate <= 0)
                {
                    MessageBox.Show("请输入有效的采样率（大于0的整数）！", "参数错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    currentStatus = AcquisitionStatus.Error;
                    UpdateStatusIndicator();
                    return;
                }
                if (!int.TryParse(SamplesPerChannelTextBox.Text, out int samplesPerChannel) || samplesPerChannel <= 0)
                {
                    MessageBox.Show("请输入有效的每通道采样数（大于0的整数）！", "参数错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    currentStatus = AcquisitionStatus.Error;
                    UpdateStatusIndicator();
                    return;
                }

                CreateAnalogInputTask(sampleRate, samplesPerChannel);

                WaveformCanvas.Children.Clear();

                isAcquiring = true;
                StartButton.IsEnabled = false;
                StopButton.IsEnabled = true;
                StatusTextBlock.Text = "正在采集和输出数据...";
                currentStatus = AcquisitionStatus.Running;
                UpdateStatusIndicator();

                reader.SynchronizeCallbacks = true;
                reader.BeginReadMultiSample(samplesPerChannel, OnReadComplete, null);
            }
            catch (DaqException ex)
            {
                MessageBox.Show($"启动采集失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusTextBlock.Text = "采集失败";
                currentStatus = AcquisitionStatus.Error;
                UpdateStatusIndicator();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"启动采集时发生未知错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusTextBlock.Text = "采集失败";
                currentStatus = AcquisitionStatus.Error;
                UpdateStatusIndicator();
            }
        }

        /// <summary>
        /// 停止按钮点击事件
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                isAcquiring = false;

                if (analogInputTask != null)
                {
                    // 先停止任务再释放，避免线程冲突
                    if (!analogInputTask.IsDone)
                    {
                        analogInputTask.Stop();
                    }
                    analogInputTask.Dispose();
                    analogInputTask = null; // 显式置空，避免重复释放
                }
                // 清理Redis订阅（如果有）
                redisManager.Unsubscribe(RedisConfig.DataChannel);

                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                StatusTextBlock.Text = "已停止采集和输出";
                currentStatus = AcquisitionStatus.Stopped;
                UpdateStatusIndicator();
            }
            catch (DaqException ex)
            {
                MessageBox.Show($"停止采集失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusTextBlock.Text = "停止失败";
                currentStatus = AcquisitionStatus.Error;
                UpdateStatusIndicator();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"停止采集时发生未知错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusTextBlock.Text = "停止失败";
                currentStatus = AcquisitionStatus.Error;
                UpdateStatusIndicator();
            }
            finally
            {
                // 确保资源释放
                analogInputTask = null;
            }
        }

        /// <summary>
        /// 模拟量任务
        /// </summary>
        /// <param name="sampleRate"></param>
        /// <param name="samplesPerChannel"></param>
        private void CreateAnalogInputTask(int sampleRate, int samplesPerChannel)
        {
            try
            {
                var devices = DaqSystem.Local.Devices;
                if (devices == null)
                {
                    throw new Exception("未找到DAQ设备，请检查硬件连接");
                }
                analogInputTask = new NationalInstruments.DAQmx.Task("AI Task");

                for (int i = 0; i < ChannelCount; i++)
                {
                    AITerminalConfiguration config = AITerminalConfiguration.Nrse;

                    analogInputTask.AIChannels.CreateVoltageChannel(
                        $"Dev1/ai{i}",
                        $"Channel{i}",
                        config,
                        0.0, 10.0,
                        AIVoltageUnits.Volts);
                }

                analogInputTask.Timing.ConfigureSampleClock(
                    "",
                    sampleRate,
                    SampleClockActiveEdge.Rising,
                    SampleQuantityMode.ContinuousSamples,
                    samplesPerChannel);

                reader = new AnalogMultiChannelReader(analogInputTask.Stream);
                currentStatus = AcquisitionStatus.Stopped;
                UpdateStatusIndicator();
            }
            catch (DaqException ex)
            {
                MessageBox.Show($"创建数据采集任务失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                currentStatus = AcquisitionStatus.Error;
                UpdateStatusIndicator();
                throw;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"创建数据采集任务时发生未知错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                currentStatus = AcquisitionStatus.Error;
                UpdateStatusIndicator();
            }
        }

        /// <summary>
        /// 更新图表
        /// </summary>
        private void UpdateWaveformDisplay()
        {
            if (latestData == null) return;
            int samples = latestData.GetLength(1);
            double canvasWidth = WaveformCanvas.ActualWidth;
            double canvasHeight = WaveformCanvas.ActualHeight;
            if (canvasWidth <= 0 || canvasHeight <= 0) return; // 避免无效尺寸

            // 清理现有控件
            WaveformCanvas.Children.Clear();

            foreach (var channel in channelLines)
            {
                channel.Clear();
            }

            double channelHeight = canvasHeight / (ChannelCount + 1);

            if (AutoScaleCheckBox.IsChecked == true)
            {
                double maxValue = 0;
                for (int ch = 0; ch < ChannelCount; ch++)
                {
                    for (int s = 0; s < samples; s++)
                    {
                        maxValue = Math.Max(maxValue, Math.Abs(latestData[ch, s]));
                    }
                }

                if (maxValue > 0)
                {
                    yScale = (channelHeight * 0.8) / maxValue / 2;
                }
            }

            int selectedChannel = ChannelComboBox.SelectedIndex - 1;

            for (int ch = 0; ch < ChannelCount; ch++)
            {
                if (selectedChannel >= 0 && ch != selectedChannel)
                    continue;

                double yOffset = (ch + 1) * channelHeight;
                Color channelColor = GetChannelColor(ch);
                string channelName = $"通道{ch}";

                AddTextBlockToCanvas(channelName, channelColor, new Thickness(5, 0, 0, 0), yOffset - 7, 5);

                double currentValue = latestData[ch, samples - 1];
                AddTextBlockToCanvas($"{currentValue:F2} V", channelColor, new Thickness(0, 0, 5, 0), yOffset - 7, null, true);

                for (int s = 0; s < samples - 1; s++)
                {
                    Line line = new Line
                    {
                        X1 = s * canvasWidth / samples,
                        Y1 = yOffset - latestData[ch, s] * yScale,
                        X2 = (s + 1) * canvasWidth / samples,
                        Y2 = yOffset - latestData[ch, s + 1] * yScale,
                        Stroke = new SolidColorBrush(channelColor),
                        StrokeThickness = 1
                    };

                    WaveformCanvas.Children.Add(line);
                    channelLines[ch].Add(line);
                }
            }
        }

        private void AddTextBlockToCanvas(string text, Color color, Thickness margin, double top, double? left = null, bool setRight = false)
        {
            TextBlock textBlock = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(color),
                FontSize = 10,
                Margin = margin
            };
            Canvas.SetTop(textBlock, top);
            if (setRight)
            {
                Canvas.SetRight(textBlock, 5);
            }
            else
            {
                Canvas.SetLeft(textBlock, left.Value);
            }
            WaveformCanvas.Children.Add(textBlock);
        }

        private Color GetChannelColor(int channelIndex)
        {
            Color[] colors = new Color[]
            {
                Colors.Red, Colors.Green, Colors.Blue, Colors.Yellow,
                Colors.Cyan, Colors.Magenta, Colors.Orange, Colors.Purple,
                Colors.Lime, Colors.Teal, Colors.Maroon, Colors.Olive,
                Colors.Navy, Colors.Gray, Colors.Silver, Colors.Brown
            };

            return colors[channelIndex % colors.Length];
        }

        protected override void OnClosed(EventArgs e)
        {
            // 强制停止采集
            if (isAcquiring)
            {
                StopButton_Click(null, null);
            }

            // 释放Redis连接
            redisManager?.Dispose();

            // 停止并释放定时器
            if (statusTimer != null)
            {
                statusTimer.Elapsed -= StatusTimer_Elapsed; // 移除事件订阅
                statusTimer.Stop();
                statusTimer.Dispose();
                statusTimer = null;
            }

            base.OnClosed(e);
            Application.Current.Shutdown();
        }
    }
}