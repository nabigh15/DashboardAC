namespace DashboardAC.Models
{
    public class LineData
    {
        public int TotalPlan { get; set; }
        public int TotalActual { get; set; }
        public int DailyPlan { get; set; }
        public double QualityRate { get; set; }
        public int TotalDefects { get; set; }
    }
}