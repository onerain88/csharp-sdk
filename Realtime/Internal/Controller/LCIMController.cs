﻿using System.Threading.Tasks;
using LeanCloud.Realtime.Internal.Protocol;
using LeanCloud.Realtime.Internal.Connection;

namespace LeanCloud.Realtime.Internal.Controller {
    internal abstract class LCIMController {
        protected LCIMClient Client {
            get; set;
        }

        internal LCIMController(LCIMClient client) {
            Client = client;
        }

        internal abstract Task OnNotification(GenericCommand notification);

        protected LCConnection Connection {
            get {
                return Client.Connection;
            }
        }

        protected GenericCommand NewCommand(CommandType cmd, OpType op) {
            GenericCommand command = NewCommand(cmd);
            command.Op = op;
            return command;
        }

        protected GenericCommand NewCommand(CommandType cmd) {
            return new GenericCommand {
                Cmd = cmd,
                AppId = LCApplication.AppId,
                PeerId = Client.Id,
            };
        }
    }
}
