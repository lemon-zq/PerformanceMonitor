using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LiveCharts;
using LiveCharts.Defaults;
using Microsoft.VisualBasic.Devices;
using Newtonsoft.Json;

namespace PerformanceMonitor
{
    public partial class MainWindow : Window, INotifyPropertyChanged, IDisposable
    {
        // 原有变量和属性保持不变...
        #region 基础监控变量
        private PerformanceCounter _cpuCounter;
        private PerformanceCounter _memoryCounter;
        private readonly Timer _updateTimer;
        private long _lastNetworkBytes;
        private DateTime _lastNetworkTime;
        private readonly long _networkMaxSpeed = 100 * 1024 * 1024; // 假设最大网络速度100MB/s
        #endregion

        #region 绑定属性
        // 基础监控数据
        public double CpuUsage { get; set; }
        public double MemoryUsage { get; set; }
        public double NetworkUsage { get; set; }
        public string CpuInfo { get; set; }
        public string MemoryInfo { get; set; }
        public string NetworkInfo { get; set; }
        public string CpuCoreInfo { get; set; }
        public DateTime UpdateTime { get; set; }

        // 颜色属性
        public Brush CpuColor => CpuUsage > 80 ? Brushes.Red : Brushes.LightBlue;
        public Brush MemoryColor => MemoryUsage > 80 ? Brushes.Red : Brushes.LightGreen;

        // 图表数据
        public ChartValues<double> CpuValues { get; set; } = new ChartValues<double>();
        public ChartValues<double> MemoryValues { get; set; } = new ChartValues<double>();
        public ChartValues<double> NetworkValues { get; set; } = new ChartValues<double>();
        public List<string> TimeLabels { get; set; } = new List<string>();

        // 多磁盘数据
        public ObservableCollection<DiskDriveInfo> DiskDrives { get; set; } = new ObservableCollection<DiskDriveInfo>();

        // 报警相关
        public string AlarmMessage { get; set; }
        public Brush AlarmBackground { get; set; } = Brushes.Red;
        public Visibility AlarmVisibility { get; set; } = Visibility.Collapsed;

        // 报警阈值
        private double _cpuAlarmThreshold = 80;
        private double _memoryAlarmThreshold = 80;
        #endregion

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            // 初始化性能计数器
            InitializePerformanceCounters();

            // 获取CPU核心信息
            GetCpuCoreInfo();

            // 初始化网络监控
            InitializeNetworkMonitor();

            // 初始化磁盘监控
            LoadDiskDrives();

            // 设置定时器（1秒更新一次）
            _updateTimer = new Timer(1000);
            _updateTimer.Elapsed += OnTimerElapsed;
            _updateTimer.Start();
        }

        // 原有方法保持不变...
        #region 初始化方法
        /// <summary>
        /// 初始化性能计数器
        /// </summary>
        private void InitializePerformanceCounters()
        {
            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cpuCounter.NextValue(); // 预热

                _memoryCounter = new PerformanceCounter("Memory", "% Committed Bytes In Use");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"初始化性能计数器失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 获取CPU核心信息
        /// </summary>
        private void GetCpuCoreInfo()
        {
            try
            {
                // 方法1：使用WMI（需要System.Management）
                var cpuInfo = new ManagementObjectSearcher("SELECT * FROM Win32_Processor").Get().Cast<ManagementObject>().First();
                int physicalCores = int.Parse(cpuInfo["NumberOfCores"].ToString());
                int logicalCores = int.Parse(cpuInfo["NumberOfLogicalProcessors"].ToString());
                CpuCoreInfo = $"物理核心: {physicalCores} | 逻辑核心: {logicalCores}";
            }
            catch
            {
                // 方法2：备用方案（仅获取逻辑核心数，无需额外引用）
                int logicalCores = Environment.ProcessorCount;
                CpuCoreInfo = $"逻辑核心: {logicalCores} (物理核心获取失败)";
            }
        }

        /// <summary>
        /// 初始化网络监控
        /// </summary>
        private void InitializeNetworkMonitor()
        {
            _lastNetworkBytes = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Sum(n => n.GetIPv4Statistics().BytesReceived + n.GetIPv4Statistics().BytesSent);
            _lastNetworkTime = DateTime.Now;
        }

        /// <summary>
        /// 加载所有磁盘驱动器
        /// </summary>
        private void LoadDiskDrives()
        {
            DiskDrives.Clear();
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.IsReady && drive.DriveType == DriveType.Fixed)
                {
                    DiskDrives.Add(new DiskDriveInfo
                    {
                        DriveName = drive.Name,
                        Usage = CalculateDriveUsage(drive),
                        Info = GetDriveInfoText(drive)
                    });
                }
            }
        }
        #endregion

        #region 数据更新方法
        /// <summary>
        /// 定时器触发事件
        /// </summary>
        private void OnTimerElapsed(object sender, ElapsedEventArgs e)
        {
            Dispatcher.Invoke(UpdateAllPerformanceData, DispatcherPriority.Normal);
        }

        /// <summary>
        /// 更新所有性能数据
        /// </summary>
        private void UpdateAllPerformanceData()
        {
            try
            {
                // 更新基础监控数据
                UpdateCpuData();
                UpdateMemoryData();
                UpdateNetworkData();
                UpdateDiskData();

                // 更新图表数据
                UpdateChartData();

                // 检查报警条件
                CheckAlarms();

                // 更新时间
                UpdateTime = DateTime.Now;

                // 通知UI更新
                NotifyPropertyChanged();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新数据失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 更新CPU数据
        /// </summary>
        private void UpdateCpuData()
        {
            CpuUsage = Math.Round(_cpuCounter.NextValue(), 1);
            CpuInfo = $"CPU使用率: {CpuUsage}%";
        }

        /// <summary>
        /// 更新内存数据
        /// </summary>
        private void UpdateMemoryData()
        {
            MemoryUsage = Math.Round(_memoryCounter.NextValue(), 1);

            var computerInfo = new ComputerInfo();
            var totalMemory = computerInfo.TotalPhysicalMemory;
            var availableMemory = computerInfo.AvailablePhysicalMemory;
            var usedMemory = totalMemory - availableMemory;

            var usedGb = Math.Round(usedMemory / (1024.0 * 1024.0 * 1024.0), 1);
            var totalGb = Math.Round(totalMemory / (1024.0 * 1024.0 * 1024.0), 1);

            MemoryInfo = $"{usedGb}GB / {totalGb}GB ({MemoryUsage}%)";
        }

        /// <summary>
        /// 更新网络数据
        /// </summary>
        private void UpdateNetworkData()
        {
            var currentBytes = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Sum(n => n.GetIPv4Statistics().BytesReceived + n.GetIPv4Statistics().BytesSent);

            var timeSpan = DateTime.Now - _lastNetworkTime;
            var bytesPerSecond = (currentBytes - _lastNetworkBytes) / timeSpan.TotalSeconds;

            // 计算网络使用率（相对于最大速度）
            NetworkUsage = Math.Round((bytesPerSecond / _networkMaxSpeed) * 100, 1);
            NetworkUsage = Math.Min(NetworkUsage, 100); // 限制最大值

            // 格式化网络速度显示
            string speedText;
            if (bytesPerSecond > 1024 * 1024)
                speedText = $"{Math.Round(bytesPerSecond / (1024 * 1024), 1)} MB/s";
            else if (bytesPerSecond > 1024)
                speedText = $"{Math.Round(bytesPerSecond / 1024, 1)} KB/s";
            else
                speedText = $"{Math.Round(bytesPerSecond, 1)} B/s";

            NetworkInfo = $"{speedText} ({NetworkUsage}%)";

            _lastNetworkBytes = currentBytes;
            _lastNetworkTime = DateTime.Now;
        }

        /// <summary>
        /// 更新磁盘数据
        /// </summary>
        private void UpdateDiskData()
        {
            foreach (var driveInfo in DiskDrives)
            {
                var drive = new DriveInfo(driveInfo.DriveName);
                if (drive.IsReady)
                {
                    driveInfo.Usage = CalculateDriveUsage(drive);
                    driveInfo.Info = GetDriveInfoText(drive);
                }
            }
        }

        /// <summary>
        /// 更新图表数据
        /// </summary>
        private void UpdateChartData()
        {
            // 添加新数据
            CpuValues.Add(CpuUsage);
            MemoryValues.Add(MemoryUsage);
            NetworkValues.Add(NetworkUsage);

            // 添加时间标签
            TimeLabels.Add(DateTime.Now.ToString("ss"));

            // 限制数据点数量（只保留最近60秒）
            const int maxPoints = 60;
            if (CpuValues.Count > maxPoints)
            {
                CpuValues.RemoveAt(0);
                MemoryValues.RemoveAt(0);
                NetworkValues.RemoveAt(0);
                TimeLabels.RemoveAt(0);
            }
        }
        #endregion

        #region 辅助方法
        /// <summary>
        /// 计算磁盘使用率
        /// </summary>
        private double CalculateDriveUsage(DriveInfo drive)
        {
            var usedSpace = drive.TotalSize - drive.AvailableFreeSpace;
            return Math.Round((double)usedSpace / drive.TotalSize * 100, 1);
        }

        /// <summary>
        /// 获取磁盘信息文本
        /// </summary>
        private string GetDriveInfoText(DriveInfo drive)
        {
            var usedSpace = drive.TotalSize - drive.AvailableFreeSpace;
            var usedGb = Math.Round(usedSpace / (1024.0 * 1024.0 * 1024.0), 1);
            var totalGb = Math.Round(drive.TotalSize / (1024.0 * 1024.0 * 1024.0), 1);
            return $"{usedGb}GB / {totalGb}GB ({CalculateDriveUsage(drive)}%)";
        }

        /// <summary>
        /// 检查报警条件
        /// </summary>
        private void CheckAlarms()
        {
            var alarmMessages = new List<string>();

            if (CpuUsage > _cpuAlarmThreshold)
                alarmMessages.Add($"CPU使用率超过阈值: {CpuUsage}%");

            if (MemoryUsage > _memoryAlarmThreshold)
                alarmMessages.Add($"内存使用率超过阈值: {MemoryUsage}%");

            if (alarmMessages.Any())
            {
                AlarmMessage = string.Join(" | ", alarmMessages);
                AlarmVisibility = Visibility.Visible;
            }
            else
            {
                AlarmVisibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 通知属性变更
        /// </summary>
        private void NotifyPropertyChanged(params string[] propertyNames)
        {
            var props = propertyNames.Any() ? propertyNames : new[]
            {
                nameof(CpuUsage), nameof(MemoryUsage), nameof(NetworkUsage),
                nameof(CpuInfo), nameof(MemoryInfo), nameof(NetworkInfo),
                nameof(CpuColor), nameof(MemoryColor), nameof(UpdateTime),
                nameof(AlarmMessage), nameof(AlarmVisibility)
            };

            foreach (var prop in props)
            {
                OnPropertyChanged(prop);
            }
        }
        #endregion

        #region 事件处理方法
        /// <summary>
        /// 导出数据按钮点击事件
        /// </summary>
        private void ExportData_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var saveFileDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "JSON文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                    FileName = $"性能监控数据_{DateTime.Now:yyyyMMddHHmmss}.json",
                    DefaultExt = ".json"
                };

                if (saveFileDialog.ShowDialog() == true)
                {
                    var exportData = new
                    {
                        ExportTime = DateTime.Now,
                        CpuUsage = this.CpuUsage,
                        MemoryUsage = this.MemoryUsage,
                        NetworkUsage = this.NetworkUsage,
                        CpuCoreInfo = this.CpuCoreInfo,
                        DiskDrives = this.DiskDrives.ToList(),
                        HistoricalData = new
                        {
                            CpuValues = this.CpuValues.ToList(),
                            MemoryValues = this.MemoryValues.ToList(),
                            NetworkValues = this.NetworkValues.ToList()
                        }
                    };

                    File.WriteAllText(saveFileDialog.FileName, JsonConvert.SerializeObject(exportData, Formatting.Indented));
                    MessageBox.Show("数据导出成功！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出数据失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 报警设置按钮点击事件
        /// </summary>
        private void AlarmSettings_Click(object sender, RoutedEventArgs e)
        {
            var window = new Window
            {
                Title = "报警阈值设置",
                Width = 300,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };

            var stackPanel = new StackPanel { Margin = new Thickness(20) };

            // CPU阈值设置
            var cpuSlider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = _cpuAlarmThreshold,
                Width = 200,
                Margin = new Thickness(0, 10, 0, 10)
            };
            var cpuText = new TextBlock { Text = $"CPU报警阈值: {_cpuAlarmThreshold}%" };
            cpuSlider.ValueChanged += (s, args) =>
            {
                _cpuAlarmThreshold = Math.Round(cpuSlider.Value, 0);
                cpuText.Text = $"CPU报警阈值: {_cpuAlarmThreshold}%";
            };

            // 内存阈值设置
            var memorySlider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = _memoryAlarmThreshold,
                Width = 200,
                Margin = new Thickness(0, 10, 0, 10)
            };
            var memoryText = new TextBlock { Text = $"内存报警阈值: {_memoryAlarmThreshold}%" };
            memorySlider.ValueChanged += (s, args) =>
            {
                _memoryAlarmThreshold = Math.Round(memorySlider.Value, 0);
                memoryText.Text = $"内存报警阈值: {_memoryAlarmThreshold}%";
            };

            // 确认按钮
            var confirmBtn = new Button
            {
                Content = "确认",
                Width = 80,
                Margin = new Thickness(0, 20, 0, 0)
            };
            confirmBtn.Click += (s, args) => window.Close();

            // 添加控件
            stackPanel.Children.Add(new TextBlock { Text = "设置报警阈值（%）" });
            stackPanel.Children.Add(cpuText);
            stackPanel.Children.Add(cpuSlider);
            stackPanel.Children.Add(memoryText);
            stackPanel.Children.Add(memorySlider);
            stackPanel.Children.Add(confirmBtn);

            window.Content = stackPanel;
            window.ShowDialog();
        }

        #region 窗口控制事件（新增）
        /// <summary>
        /// 标题栏拖动窗口
        /// </summary>
        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                // 如果是最大化状态，先还原再拖动
                if (WindowState == WindowState.Maximized)
                {
                    // 计算还原后的位置
                    var point = PointToScreen(e.MouseDevice.GetPosition(this));
                    WindowState = WindowState.Normal;
                    Left = point.X - (Width / 2);
                    Top = point.Y - 15;
                }
                DragMove();
            }
        }

        /// <summary>
        /// 最小化按钮点击
        /// </summary>
        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        /// <summary>
        /// 最大化/还原按钮点击
        /// </summary>
        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Normal)
            {
                WindowState = WindowState.Maximized;
                btnMaximize.Content = "▢"; // 切换为还原图标
            }
            else
            {
                WindowState = WindowState.Normal;
                btnMaximize.Content = "□"; // 切换为最大化图标
            }
        }

        /// <summary>
        /// 关闭按钮点击
        /// </summary>
        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
        #endregion
        #endregion

        #region 接口实现
        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void Dispose()
        {
            _updateTimer?.Stop();
            _updateTimer?.Dispose();
            _cpuCounter?.Dispose();
            _memoryCounter?.Dispose();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            Dispose();
        }
        #endregion
    }

    #region 辅助类
    /// <summary>
    /// 磁盘驱动器信息类
    /// </summary>
    public class DiskDriveInfo : INotifyPropertyChanged
    {
        private string _driveName;
        private double _usage;
        private string _info;

        public string DriveName
        {
            get => _driveName;
            set { _driveName = value; OnPropertyChanged(); }
        }

        public double Usage
        {
            get => _usage;
            set { _usage = value; OnPropertyChanged(); }
        }

        public string Info
        {
            get => _info;
            set { _info = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// 进度条值转缩放比例转换器（可选）
    /// </summary>
    public class ProgressToScaleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            double progress = (double)value;
            return progress / 100; // 将 0-100 转为 0-1
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    #endregion
}