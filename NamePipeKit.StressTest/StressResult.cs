// StressResult.cs —— 不变
using System.Collections.Generic;

namespace NamePipeKit.StressTest
{
    public sealed class StressResult
    {
        public long TotalRequests { get; set; }
        public long Succeeded { get; set; }
        public long Failed { get; set; }
        public System.TimeSpan Elapsed { get; set; }
        public double Qps { get; set; }
        public double MinMs { get; set; }
        public double MaxMs { get; set; }
        public double AvgMs { get; set; }
        public double P50Ms { get; set; }
        public double P90Ms { get; set; }
        public double P95Ms { get; set; }
        public double P99Ms { get; set; }
        public List<string> ErrorSamples { get; set; } = new List<string>();

        public static double Percentile(double[] sorted, double p)
        {
            if (sorted == null || sorted.Length == 0) return 0;
            if (p <= 0) return sorted[0];
            if (p >= 100) return sorted[sorted.Length - 1];
            var rank = (p / 100.0) * (sorted.Length - 1);
            var lo = (int)System.Math.Floor(rank);
            var hi = (int)System.Math.Ceiling(rank);
            if (lo == hi) return sorted[lo];
            var w = rank - lo;
            return sorted[lo] * (1 - w) + sorted[hi] * w;
        }
    }
}