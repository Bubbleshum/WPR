using System;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;

namespace GameRun
{
    /// <summary>GAMERUN_ALLOC=1: AllocationTick sampling by type, printed to stderr every 5 s.</summary>
    internal sealed class AllocListener : EventListener
    {
        private readonly ConcurrentDictionary<string, long> _bytes = new();
        private Timer _timer;

        public static void StartIfAsked()
        {
            if (Environment.GetEnvironmentVariable("GAMERUN_ALLOC") == "1")
            {
                var l = new AllocListener();
                l._timer = new Timer(_ => l.Dump(), null, 5000, 5000);
            }
        }

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
            {
                EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventName == null || !e.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal))
            {
                return;
            }

            int ti = e.PayloadNames.IndexOf("TypeName");
            int ki = e.PayloadNames.IndexOf("AllocationKind");
            int ai = e.PayloadNames.IndexOf("AllocationAmount64");
            string type = ti >= 0 ? e.Payload[ti]?.ToString() : "?";
            string kind = ki >= 0 ? e.Payload[ki]?.ToString() : "?";
            long amount = ai >= 0 ? Convert.ToInt64(e.Payload[ai]) : 100_000;
            _bytes.AddOrUpdate($"{kind} {type}", amount, (_, v) => v + amount);
        }

        private void Dump()
        {
            var top = _bytes.ToArray().OrderByDescending(p => p.Value).Take(12).ToArray();
            _bytes.Clear();
            Console.Error.WriteLine("[alloc] " + DateTime.Now.ToString("HH:mm:ss") + " " + string.Join(" | ", top.Select(p => $"{p.Key}={p.Value / (1024 * 1024)}MB")));
        }
    }
}
