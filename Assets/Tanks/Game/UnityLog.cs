using UnityEngine;

namespace Tanks.Game
{
    /// <summary>
    /// ILog over Debug.Log, so Tanks.Protocol stays Unity-free while its output still
    /// lands in the Console. Called from the net thread — Debug.Log is one of the few
    /// Unity APIs that's safe off the main thread.
    /// </summary>
    public sealed class UnityLog : ILog
    {
        private readonly LogLevel _minLevel;

        public UnityLog(LogLevel minLevel = LogLevel.Debug)
        {
            _minLevel = minLevel;
        }

        public bool IsEnabled(LogLevel level) => level >= _minLevel && level < LogLevel.None;

        public void Log(ref LogMessage message)
        {
            string text;
            if (message.State is string s) text = s;
            else if (message.State is char[] chars) text = new string(chars, 0, message.Length);
            else if (message.State is string[] parts) text = string.Join("", parts, 0, message.Length);
            else text = "";

            LogLevel level = message.Level;
            message.Return();
            Write(level, text);
        }

        public void LogTrace(string message) { if (IsEnabled(LogLevel.Trace)) Write(LogLevel.Trace, message); }
        public void LogDebug(string message) { if (IsEnabled(LogLevel.Debug)) Write(LogLevel.Debug, message); }
        public void LogInformation(string message) { if (IsEnabled(LogLevel.Information)) Write(LogLevel.Information, message); }
        public void LogWarning(string message) { if (IsEnabled(LogLevel.Warning)) Write(LogLevel.Warning, message); }
        public void LogError(string message) { if (IsEnabled(LogLevel.Error)) Write(LogLevel.Error, message); }

        private static void Write(LogLevel level, string message)
        {
            string line = $"[Net] {message}";
            switch (level)
            {
                case LogLevel.Warning: Debug.LogWarning(line); break;
                case LogLevel.Error: Debug.LogError(line); break;
                default: Debug.Log(line); break;
            }
        }
    }
}
