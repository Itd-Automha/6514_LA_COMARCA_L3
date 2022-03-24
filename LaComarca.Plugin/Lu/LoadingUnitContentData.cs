using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LaComarca.Plugin.Lu
{
    public class LoadingUnitContentData
    {
        public int Id { get; set; }
        public string SSCC { get; set; }
        public string Id_reference { get; set; }        //sku id
        public decimal QT { get; set; }
        public string batch { get; set; }
        public DateTime? Expiry_Date { get; set; }
        public int SYNC { get; set; }
        public string SYNC_Date { get; set; }

    }
}
