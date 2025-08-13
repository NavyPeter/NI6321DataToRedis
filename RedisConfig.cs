using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WpfApp2
{
    public class RedisConfig
    {
        public const string ConnectionString = "127.0.0.1:6379,password=123456 "; // Redis连接字符串
        public const string DataChannel = "Daq.data.updated"; // 数据更新通知频道
        public const string DataKeyPrefix = "Daq:data:"; // 数据存储Key前缀
    }
}