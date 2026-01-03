using System;
using System.Diagnostics;
using ChatTwo.Code;

namespace ChatTwo.DM;

/// <summary>
/// Simple performance testing utility for DM windows.
/// </summary>
internal static class DMWindowPerformanceTest
{
    private static readonly Stopwatch _stopwatch = new();
    private static long _lastFrameTime = 0;
    private static int _frameCount = 0;
    private static double _totalFrameTime = 0;
    private static double _maxFrameTime = 0;
    
    /// <summary>
    /// Starts performance monitoring for a DM window frame.
    /// </summary>
    public static void StartFrame()
    {
        _stopwatch.Restart();
    }
    
    /// <summary>
    /// Ends performance monitoring for a DM window frame and logs if performance is poor.
    /// </summary>
    /// <param name="windowName">Name of the DM window for logging</param>
    public static void EndFrame(string windowName)
    {
        _stopwatch.Stop();
        var frameTimeMs = _stopwatch.Elapsed.TotalMilliseconds;
        
        _frameCount++;
        _totalFrameTime += frameTimeMs;
        _maxFrameTime = Math.Max(_maxFrameTime, frameTimeMs);
        
        // Log if frame time is excessive (> 16.67ms = 60 FPS threshold)
        if (frameTimeMs > 16.67)
        {
            Plugin.Log.Warning($"DMWindow Performance: {windowName} frame took {frameTimeMs:F2}ms (target: 16.67ms for 60 FPS)");
        }
        
        // Log performance summary every 300 frames (about 5 seconds at 60 FPS)
        if (_frameCount % 300 == 0)
        {
            var avgFrameTime = _totalFrameTime / _frameCount;
            Plugin.Log.Info($"DMWindow Performance Summary for {windowName}: Avg: {avgFrameTime:F2}ms, Max: {_maxFrameTime:F2}ms over {_frameCount} frames");
            
            // Reset counters
            _frameCount = 0;
            _totalFrameTime = 0;
            _maxFrameTime = 0;
        }
    }
    
    /// <summary>
    /// Measures the time taken by a specific operation.
    /// </summary>
    /// <param name="operation">The operation to measure</param>
    /// <param name="operationName">Name for logging</param>
    public static void MeasureOperation(Action operation, string operationName)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            operation();
        }
        finally
        {
            sw.Stop();
            var timeMs = sw.Elapsed.TotalMilliseconds;
            
            // Log if operation takes more than 1ms
            if (timeMs > 1.0)
            {
                Plugin.Log.Debug($"DMWindow Operation: {operationName} took {timeMs:F2}ms");
            }
        }
    }
}