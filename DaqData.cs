using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WpfApp2
{
    public class DaqData
    {
        public string ChannelID { get; set; }
        public double Value { get; set; }
        public DateTime Timestamp { get; set; }
        public string Status { get; set; }
    }
}