using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ChatTwo.DM;

/// <summary>
/// Simple performance profiler to measure FPS impact of DM windows.
/// Helps identify the exact performance difference between different modes.
/// </summary>
internal static class DMPerformanceProfiler
{
    private static readonly Dictionary<string, PerformanceMetrics> Metrics = new();
    private static readonly Stopwatch GlobalTimer = Stopwatch.StartNew();
    private static long LastFPSReport = 0;
    private static int FrameCount = 0;
    private static readonly List<double> RecentFrameTimes = new();
    private static readonly object MetricsLock = new();

    private class PerformanceMetrics
    {
        public double TotalTime { get; set; }
        public int CallCount { get; set; }
        public double MinTime { get; set; } = double.MaxValue;
        public double MaxTime { get; set; }
        public double AverageTime => CallCount > 0 ? TotalTime / CallCount : 0;
    }

    /// <summary>
    /// Starts measuring performance for a specific operation.
    /// </summary>
    public static IDisposable MeasureOperation(string operationName)
    {
        if (!Plugin.Config.EnableDMPerformanceLogging)
            return new NoOpDisposable();

        return new PerformanceMeasurement(operationName);
    }

    /// <summary>
    /// Records a frame time for FPS calculation.
    /// </summary>
    public static void RecordFrameTime(double frameTimeMs)
    {
        if (!Plugin.Config.EnableDMPerformanceLogging)
            return;

        lock (MetricsLock)
        {
            RecentFrameTimes.Add(frameTimeMs);
            FrameCount++;

            // Keep only recent frame times (last 60 frames for 1-second average)
            if (RecentFrameTimes.Count > 60)
            {
                RecentFrameTimes.RemoveAt(0);
            }

            // Report FPS every 5 seconds
            var currentTime = GlobalTimer.ElapsedMilliseconds;
            if (currentTime - LastFPSReport >= 5000)
            {
                ReportFPS();
                LastFPSReport = currentTime;
            }
        }
    }

    /// <summary>
    /// Records performance data for an operation.
    /// </summary>
    private static void RecordMetric(string operationName, double timeMs)
    {
        lock (MetricsLock)
        {
            if (!Metrics.TryGetValue(operationName, out var metric))
            {
                metric = new PerformanceMetrics();
                Metrics[operationName] = metric;
            }

            metric.TotalTime += timeMs;
            metric.CallCount++;
            metric.MinTime = Math.Min(metric.MinTime, timeMs);
            metric.MaxTime = Math.Max(metric.MaxTime, timeMs);
        }
    }

    /// <summary>
    /// Reports current FPS and performance metrics.
    /// </summary>
    private static void ReportFPS()
    {
        if (RecentFrameTimes.Count == 0)
            return;

        var averageFrameTime = RecentFrameTimes.Average();
        var fps = 1000.0 / averageFrameTime;
        var minFrameTime = RecentFrameTimes.Min();
        var maxFrameTime = RecentFrameTimes.Max();
        var maxFPS = 1000.0 / minFrameTime;
        var minFPS = 1000.0 / maxFrameTime;

        Plugin.Log.Info($"DM Performance Report:");
        Plugin.Log.Info($"  Average FPS: {fps:F1} (frame time: {averageFrameTime:F2}ms)");
        Plugin.Log.Info($"  FPS Range: {minFPS:F1} - {maxFPS:F1}");
        Plugin.Log.Info($"  Frame Count: {FrameCount}");

        // Report operation metrics
        foreach (var kvp in Metrics.ToList())
        {
            var metric = kvp.Value;
            Plugin.Log.Info($"  {kvp.Key}: avg={metric.AverageTime:F2}ms, calls={metric.CallCount}, range={metric.MinTime:F2}-{metric.MaxTime:F2}ms");
        }

        // Warn if FPS is below expected thresholds
        if (fps < 60)
        {
            Plugin.Log.Warning($"DM Window FPS below 60: {fps:F1} FPS. Consider enabling aggressive performance mode.");
        }
        else if (fps < 100)
        {
            Plugin.Log.Info($"DM Window FPS good but could be better: {fps:F1} FPS. Aggressive mode might help.");
        }
        else
        {
            Plugin.Log.Info($"DM Window FPS excellent: {fps:F1} FPS");
        }
    }

    /// <summary>
    /// Gets a performance summary for display in UI.
    /// </summary>
    public static string GetPerformanceSummary()
    {
        if (!Plugin.Config.EnableDMPerformanceLogging || RecentFrameTimes.Count == 0)
            return "Performance monitoring disabled";

        lock (MetricsLock)
        {
            var averageFrameTime = RecentFrameTimes.Average();
            var fps = 1000.0 / averageFrameTime;
            return $"FPS: {fps:F1} | Frame Time: {averageFrameTime:F2}ms | Frames: {FrameCount}";
        }
    }

    /// <summary>
    /// Clears all performance metrics.
    /// </summary>
    public static void Reset()
    {
        lock (MetricsLock)
        {
            Metrics.Clear();
            RecentFrameTimes.Clear();
            FrameCount = 0;
            LastFPSReport = GlobalTimer.ElapsedMilliseconds;
        }
    }

    private class PerformanceMeasurement : IDisposable
    {
        private readonly string _operationName;
        private readonly Stopwatch _stopwatch;

        public PerformanceMeasurement(string operationName)
        {
            _operationName = operationName;
            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            RecordMetric(_operationName, _stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}