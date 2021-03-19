﻿using System;
using System.Collections.Generic;

namespace LeanCloud.Engine {
    public class LCPingHandler {
        public static object HandlePing() {
            LCLogger.Debug("Ping ~~~");
            return new Dictionary<string, string> {
                { "runtime", $"dotnet-{Environment.Version}" },
                { "version", LCApplication.SDKVersion }
            };
        }
    }
}
