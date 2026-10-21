using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DemoTextClient.Models.BigModelModels
{
    public class DevTempModels
    {
        public int Id { get; set; }

        //0还未传递外部加密    1已经传递处理
        public int Status { get; set; }
        public String TextContent { get; set; }
        public String CreateTime { get; set; }
        public String ModelId { get; set; }
        public String FileSize { get; set; }
        public String FileType { get; set; }
        public String PreLabel { get; set; }
        public String ModelType { get; set; }
        public String PreTime { get; set; }
        public String FilePath { get; set; }
        public String FileName { get; set; }
        public String FolderName { get; set; }
        public String FolderPath { get; set; }
    }
}
