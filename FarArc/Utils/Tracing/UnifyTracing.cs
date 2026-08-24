using System;
using System.Collections.Generic;
using Shawn.Utils;

namespace FarArc.Utils.Tracing
{
    internal static class UnifyTracing
    {
        public static void Init()
        {
            // External telemetry is intentionally disabled.
        }

        public static void Error(Exception e, IDictionary<string, string>? properties = null, Dictionary<string, string>? attachments = null)
        {
            SimpleLogHelper.Error(e);
            if (properties?.Count > 0)
            {
                SimpleLogHelper.Debug($"Error properties: {string.Join(", ", properties.Keys)}");
            }
            if (attachments?.Count > 0)
            {
                SimpleLogHelper.Debug($"Error attachments available locally: {string.Join(", ", attachments.Keys)}");
            }
        }

        public static void TraceSpecial(Dictionary<string, string> kys)
        {
            // No-op: usage telemetry is disabled.
        }

        public static void TraceSessionOpen(string protocol, string via)
        {
            // No-op: session telemetry is disabled.
        }
    }
}
