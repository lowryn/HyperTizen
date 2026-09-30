using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;

namespace HyperTizen
{
    // Recent log lines, kept in memory and in a file so the previous run survives a crash.
    // Readable over HTTP (GET /logs) when dlog is unavailable.
    public static class Diag
    {
        private const int MaxLines = 400;
        private static readonly ConcurrentQueue<string> _lines = new ConcurrentQueue<string>();
        private static string _previous = "";
        private static string _path;

        static Diag()
        {
            try
            {
                string dir = Tizen.Applications.Application.Current?.DirectoryInfo?.Data;
                if (string.IsNullOrEmpty(dir)) return;

                _path = Path.Combine(dir, "diag.log");
                if (File.Exists(_path))
                {
                    string[] old = File.ReadAllLines(_path);
                    _previous = string.Join("\n", old.Skip(Math.Max(0, old.Length - 150)));
                }
                File.WriteAllText(_path, "");
            }
            catch
            {
                _path = null;
            }
        }

        public static void Log(string message)
        {
            try { Tizen.Log.Debug("HyperTizen", message); } catch { }
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + message;
            _lines.Enqueue(line);
            while (_lines.Count > MaxLines && _lines.TryDequeue(out _)) { }

            if (_path == null) return;
            try { File.AppendAllText(_path, line + "\n"); } catch { }
        }

        public static string Tail(int count)
        {
            string[] all = _lines.ToArray();
            return string.Join("\n", all.Skip(Math.Max(0, all.Length - count)));
        }

        public static string Dump()
        {
            string current = string.Join("\n", _lines.ToArray());
            if (string.IsNullOrEmpty(_previous)) return current;
            return "--- previous run ---\n" + _previous + "\n--- current run ---\n" + current;
        }
    }
}
