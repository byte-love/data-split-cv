using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DemoTextClient.Models.BigModelModels
{
    public class TrainTempModels
    {
        public int id { get; set; }
        public String TextContent { get; set; }

        public String TextLabelName { get; set; }
        public String CreateTime { get; set; }
        public String Model { get; set; }
        public String FileSize { get; set; }
        public String FileType { get; set; }
        public String Status { get; set; }
        public String FilePath { get; set; }
        public String FileName { get; set; }
        public String FolderPath { get; set; }
        public String FolderName { get; set; }
    }
}
