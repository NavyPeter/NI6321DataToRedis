using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WpfApp2
{
    /// <summary>
    /// 通道配置模型
    /// </summary>
    public class ChannelConfig
    {
        public int Channel { get; set; }
        public double Slope { get; set; }
        public double Intercept { get; set; }
    }
}