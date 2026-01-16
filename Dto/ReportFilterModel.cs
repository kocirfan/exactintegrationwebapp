using System;
using System.Collections.Generic;

namespace ExactWebApp.Dto
{
    public class ReportFilterModel
    {
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string? Period { get; set; }
        public int TopCount { get; set; } = 5;
        public List<string>? CustomerNames { get; set; }
        public List<string>? ProductCodes { get; set; }
        public string? SearchTerm { get; set; }
        public double? MinAmount { get; set; }
        public double? MaxAmount { get; set; }
    }
}
