using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;
using IO.Ably.Transport;
using IO.Ably.Transport.States.Connection;
using IO.Ably.Types;
using IO.Ably.Utils;

namespace IO.Ably.Realtime.Workflow
{
    internal class RealtimeWorkflow : IQueueCommand
    {
        AblyRealtime Client { get; }
        private AblyAuth Auth => Client.RestClient.AblyAuth;
        public Connection Connection { get; }
        public RealtimeChannels Channels { get; }

        public ConnectionManager ConnectionManager => Connection.ConnectionManager;
        public ILogger Logger { get; }

        internal RealtimeState State { get; }

        private Func<DateTimeOffset> Now => Connection.Now;

        internal ConnectionHeartbeatHandler HeartbeatHandler { get; }
        internal AcknowledgementProcessor AckProcessor { get; set; }

        internal ChannelMessageProcessor ChannelMessageProcessor { get; }

        internal List<(string, Func<ProtocolMessage, RealtimeState, ValueTask<bool>>)> MessageHandlers;

        internal readonly Channel<RealtimeCommand> RealtimeMessageLoop = Channel.CreateUnbounded<RealtimeCommand>(
            new UnboundedChannelOptions()
            {
                SingleReader = true,
                SingleWriter = false
            });

        public RealtimeWorkflow(AblyRealtime client, ILogger logger)
        {
            Client = client;
            Connection = client.Connection;
            Channels = client.Channels;
            Logger = logger;
            State = new RealtimeState(client.Options.FallbackHosts?.Shuffle().ToList());

            SetInitialConnectionState();

            HeartbeatHandler = new ConnectionHeartbeatHandler(Connection.ConnectionManager, logger);
            ChannelMessageProcessor = new ChannelMessageProcessor(Channels, logger);
            AckProcessor = new AcknowledgementProcessor(Connection, State.Connection);
            MessageHandlers = new List<(string, Func<ProtocolMessage, RealtimeState, ValueTask<bool>>)>
            {
                ("State handler", (message, state) => ConnectionManager.State.OnMessageReceived(message, state)),
                ("Heartbeat handler", HeartbeatHandler.OnMessageReceived),
                ("Ack handler", (message, state) => AckProcessor.OnMessageReceived(message, state))
            };
        }

        private void SetInitialConnectionState()
        {
            var initialState = new ConnectionInitializedState(ConnectionManager, Logger);
            State.Connection.CurrentStateObject = initialState;
            SetRecoverKeyIfPresent(Client.Options.Recover);
        }


        public void Start()
        {
            Task.Run(Consume);
        }

        public void QueueCommand(params RealtimeCommand[] commands)
        {
            foreach (var command in commands)
            {
                var writeResult = RealtimeMessageLoop.Writer.TryWrite(command);
                if (writeResult == false) //Should never happen as we don't close the channels
                {
                    Logger.Warning(
                        $"Cannot schedule command: {command.Explain()} because the execution channel is closed");
                }
            }
        }

        private async Task Consume()
        {
            var reader = RealtimeMessageLoop.Reader;
            while (await reader.WaitToReadAsync())
            {
                if (reader.TryRead(out RealtimeCommand cmd))
                {
                    try
                    {
                        int level = 0;
                        var cmds = new List<RealtimeCommand> {cmd};
                        while (cmds.Count > 0)
                        {
                            if (level > 5)
                            {
                                throw new Exception("Something is wrong. There shouldn't be 5 levels of nesting");
                            }

                            var cmdsToExecute = cmds.ToArray();
                            cmds.Clear();

                            foreach (var cmdToExecute in cmdsToExecute)
                            {
                                try
                                {
                                    var result = await ProcessCommand(cmdToExecute);
                                    cmds.AddRange(result);
                                }
                                catch (Exception e)
                                {
                                    Logger.Error("Error Processing command: " + cmdsToExecute.ToString(), e);
                                }
                            }

                            level++;
                        }
                    }
                    catch (Exception e)
                    {
                        // TODO: Figure out there will be a case where the library becomes in an invalid state
                        // TODO: Emit the error on the connection object
                        Logger.Error("Error processing command: " + cmd.Explain(), e);
                    }
                }
            }
        }

        private void DelayCommandHandler(TimeSpan delay, RealtimeCommand cmd) =>
            Task.Delay(delay).ContinueWith(_ => QueueCommand(cmd));


        internal async Task<IEnumerable<RealtimeCommand>> ProcessCommand(RealtimeCommand command)
        {
            bool shouldLogCommand = !((command is EmptyCommand) || (command is ListCommand));
            try
            {
                if (Logger.IsDebug && shouldLogCommand)
                {
                    Logger.Debug("Begin - " + command.Explain());
                }

                switch (command)
                {
                    case ListCommand cmd:
                        return cmd.Commands;
                    case EmptyCommand _:
                        return Enumerable.Empty<RealtimeCommand>();
                    default:
                        var next = await ProcessCommandInner(command);
                        return new[] {next};
                }
            }
            finally
            {
                if (Logger.IsDebug && shouldLogCommand)
                {
                    Logger.Debug($"End - {command.Name}|{command.Id}");
                }
            }
        }

        /// <summary>
        /// Processes a command and return a list of commands that need to be immediately executed
        /// </summary>
        /// <param name="command"></param>
        /// <returns></returns>
        /// <exception cref="AblyException"></exception>
        internal async Task<RealtimeCommand> ProcessCommandInner(RealtimeCommand command)
        {
            switch (command)
            {
                case ConnectCommand _:
                    var nextCommand = ConnectionManager.Connect();
                    var initFailedChannelsOnConnect =
                        ChannelCommand.CreateForAllChannels(InitialiseFailedChannelsOnConnect.Create());
                    return ListCommand.Create(initFailedChannelsOnConnect, nextCommand);

                case CloseConnectionCommand _:
                    ConnectionManager.CloseConnection();
                    break;

                case RetryAuthCommand retryAuth:

                    ClearTokenAndRecordRetry();

                    if (retryAuth.UpdateState)
                    {
                        return ListCommand.Create(
                            SetDisconnectedStateCommand.Create(retryAuth.Error,
                                skipAttach: State.Connection.State == ConnectionState.Connecting),
                            SetConnectingStateCommand.Create()
                        );
                    }
                    else
                    {
                        await Auth.RenewToken();
                        return EmptyCommand.Instance;
                    }

                case SetInitStateCommand _:
                case SetConnectedStateCommand _:
                case SetConnectingStateCommand _:
                case SetFailedStateCommand _:
                case SetDisconnectedStateCommand _:
                case SetClosingStateCommand _:
                case SetSuspendedStateCommand _:
                case SetClosedStateCommand _:
                    return await HandleSetStateCommand(command);

                case ProcessMessageCommand cmd:
                    await ProcessMessage(cmd.ProtocolMessage);
                    break;
                case SendMessageCommand cmd:
                    if (State.Connection.CurrentStateObject.CanSend)
                    {
                        SendMessage(cmd.ProtocolMessage, cmd.Callback);
                    }
                    else if (State.Connection.CurrentStateObject.CanQueue)
                    {
                        Logger.Debug("Queuing message");
                        State.PendingMessages.Enqueue(new MessageAndCallback(cmd.ProtocolMessage, cmd.Callback, Logger));
                    }
                    break;
                case PingCommand cmd:
                    return ListCommand.Create(HandlePingCommand(cmd).ToArray());
                case EmptyCommand _:
                    break;
                case DelayCommand cmd:
                    DelayCommandHandler(cmd.Delay, cmd.CommandToQueue);
                    break;
                case PingTimerCommand cmd:
                    HandlePingTimer(cmd);
                    break;
                case ChannelCommand cmd:
                    await Channels.ExecuteCommand(cmd);
                    break;
                case HandleConnectingTokenErrorCommand cmd:
                    try
                    {
                        ClearTokenAndRecordRetry();

                        var host = AttemptsHelpers.GetHost(State, Client.Options.FullRealtimeHost);
                        SetNewHostInState(host);

                        await ConnectionManager.CreateTransport(host);
                        return EmptyCommand.Instance;
                    }
                    catch (AblyException ex)
                    {
                        Logger.Error("Error trying to renew token.", ex);
                        return SetDisconnectedStateCommand.Create(ex.ErrorInfo);
                    }
                case HandleConnectingFailureCommand cmd:
                    var exceptionErrorInfo = cmd.Exception != null ? new ErrorInfo(cmd.Exception.Message, 80000) : null;
                    ErrorInfo resolvedError = cmd.Error ?? exceptionErrorInfo;

                    if (State.ShouldSuspend())
                    {
                        return SetSuspendedStateCommand.Create(resolvedError ?? ErrorInfo.ReasonSuspended, cmd.ClearConnectionKey);
                    }
                    else
                    {
                        return SetDisconnectedStateCommand.Create(resolvedError ?? ErrorInfo.ReasonDisconnected, cmd.ClearConnectionKey);
                    }
                case HandleTrasportEventCommand cmd:
                    if (cmd.TransportState == TransportState.Closed || cmd.Exception != null)
                    {
                        switch (State.Connection.State)
                        {
                            case ConnectionState.Closing:
                                return SetClosedStateCommand.Create(exception: cmd.Exception);
                            case ConnectionState.Connecting:
                                return HandleConnectingFailureCommand.Create(null, cmd.Exception, false);
                            case ConnectionState.Connected:
                                var errorInfo = GetErrorInfoFromTransportException(cmd.Exception, ErrorInfo.ReasonDisconnected);
                                return SetDisconnectedStateCommand.Create(errorInfo,
                                    retryInstantly: Connection.ConnectionResumable, exception: cmd.Exception);
                        }
                    }
                    return EmptyCommand.Instance;
                default:
                    throw new AblyException("No handler found for - " + command.Explain());
            }


            async Task ProcessMessage(ProtocolMessage message)
            {
                try
                {
                    State.Connection.UpdateSerial(message);
                    State.Connection.SetConfirmedAlive(Now());

                    foreach (var (name, handler) in MessageHandlers)
                    {
                        var handled = await handler(message, State);
                        if (Logger.IsDebug)
                        {
                            Logger.Debug($"Message handler '{name}' - {(handled ? "Handled" : "Skipped")}");
                        }

                        if (handled)
                        {
                            break;
                        }
                    }

                    // Notify the Channel message processor regardless of what happened above
                    await ChannelMessageProcessor.MessageReceived(message, State);
                }
                catch (Exception e)
                {
                    Logger.Error("Error processing message: " + message, e);
                    throw new AblyException(e);
                }
            }

            ErrorInfo GetErrorInfoFromTransportException(Exception ex, ErrorInfo @default)
            {
                if (ex?.Message == "HTTP/1.1 401 Unauthorized")
                {
                    return ErrorInfo.ReasonRefused;
                }

                return @default;
            }

            return EmptyCommand.Instance;
        }

        private void SetNewHostInState(string newHost)
        {
            if (IsFallbackHost())
            {
                Client.RestClient.CustomHost = newHost;
            }

            State.Connection.Host = newHost;

            bool IsFallbackHost()
            {
                return State.Connection.FallbackHosts.Contains(newHost);
            }
        }

        private void ClearTokenAndRecordRetry()
        {
            Auth.ExpireCurrentToken();
            State.AttemptsInfo.RecordTokenRetry();
        }

        private void SendMessage(ProtocolMessage message, Action<bool, ErrorInfo> callback)
        {
            if (message.AckRequired)
            {
                message.MsgSerial = State.Connection.IncrementSerial();
                AckProcessor.Queue(message, callback);
            }

            ConnectionManager.SendToTransport(message);
        }

        private void SetRecoverKeyIfPresent(string recover)
        {
            if (recover.IsNotEmpty())
            {
                var match = TransportParams.RecoveryKeyRegex.Match(recover);
                if (match.Success && long.TryParse(match.Groups[3].Value, out long messageSerial))
                {
                    State.Connection.MessageSerial = messageSerial;
                }
                else
                {
                    Logger.Error($"Recovery Key '{recover}' could not be parsed.");
                }
            }
        }

        private void HandleConnectedCommand(SetConnectedStateCommand cmd)
        {
            var info = new ConnectionInfo(cmd.Message);

            bool resumed = State.Connection.IsResumed(info);
            bool hadPreviousConnection = State.Connection.Key.IsNotEmpty();

            State.Connection.Update(info);

            if (info.ClientId.IsNotEmpty())
            {
                Auth.ConnectionClientId = info.ClientId;
            }

            var connectedState = new ConnectionConnectedState(
                ConnectionManager,
                cmd.Message.Error,
                cmd.IsUpdate,
                Logger
            );

            SetState(connectedState);

            if (hadPreviousConnection && resumed == false)
            {
                AckProcessor.ClearQueueAndFailMessages(null);
                Logger.Warning("Force detaching all attached channels because the connection did not resume successfully!");

                foreach (var channel in Channels)
                {
                    if (channel.State == ChannelState.Attached || channel.State == ChannelState.Attaching)
                    {
                        (channel as RealtimeChannel).SetChannelState(ChannelState.Detached, cmd.Message.Error);
                    }
                }
            }

            SendPendingMessages(resumed);
        }

        private void HandlePingTimer(PingTimerCommand cmd)
        {
            var relevantRequest = State.PingRequests.FirstOrDefault(x => x.Id.EqualsTo(cmd.PingRequestId));

            if (relevantRequest != null)
            {
                // fail the request if it still exists. If it was already handled it will not be there

                relevantRequest.Callback?.Invoke(null, PingRequest.TimeOutError);

                State.PingRequests.Remove(relevantRequest);
            }
        }

        private IEnumerable<RealtimeCommand> HandlePingCommand(PingCommand cmd)
        {
            if (Connection.State != ConnectionState.Connected)
            {
                // We don't want to wait for the execution to finish
                NotifyExternalClient(
                    () => { cmd.Request.Callback?.Invoke(null, PingRequest.DefaultError); },
                    "Notifying Ping callback because connection state is not Connected");
            }
            else
            {
                yield return SendMessageCommand.Create(new ProtocolMessage(ProtocolMessage.MessageAction.Heartbeat)
                {
                    Id = cmd.Request.Id // Pass the ping request id so we can match it on the way back
                });

                // Only trigger the timer if there is a call back
                // Question: Do we trigger the error if there is no callback but then we can just emmit it

                if (cmd.Request.Callback != null)
                {
                    State.PingRequests.Add(cmd.Request);
                    yield return DelayCommand.Create(ConnectionManager.DefaultTimeout,
                        PingTimerCommand.Create(cmd.Request.Id));
                }
            }
        }

        private Task NotifyExternalClient(Action action, string reason)
        {
            try
            {
                return Task.Run(() => ActionUtils.SafeExecute(action));
            }
            catch (Exception e)
            {
                Logger.Error("Error while notifying external client for " + reason, e);
            }

            return Task.CompletedTask;
        }

        public void Close()
        {
            RealtimeMessageLoop.Writer.Complete();
        }

        public async Task<RealtimeCommand> HandleSetStateCommand(RealtimeCommand command)
        {

            try
            {
                switch (command)
                {
                    case SetInitStateCommand cmd:
                        var initState = new ConnectionInitializedState(ConnectionManager, Logger);
                        SetState(initState);
                        break;
                    case SetConnectedStateCommand cmd:
                        HandleConnectedCommand(cmd);
                        break;
                    case SetConnectingStateCommand cmd:

                        try
                        {
                            if (cmd.ClearConnectionKey)
                            {
                                State.Connection.ClearKey();
                            }

                            // RTN15g - If a client has been disconnected for longer
                            // than the connectionStateTtl, it should not attempt to resume.
                            if (State.Connection.HasConnectionStateTtlPassed(Now))
                            {
                                State.Connection.ClearKeyAndId();
                            }

                            var connectingHost = AttemptsHelpers.GetHost(State, Client.Options.FullRealtimeHost);
                            SetNewHostInState(connectingHost);

                            var connectingState = new ConnectionConnectingState(ConnectionManager, Logger);
                            SetState(connectingState);

                            await ConnectionManager.CreateTransport(connectingHost);


                            break;
                        }
                        catch (AblyException ex)
                        {
                            Logger.Error("Error setting connecting state", ex);

                            // RSA4c2 & RSA4d
                            if (ex.ErrorInfo.Code == 80019 & !ex.ErrorInfo.IsForbiddenError)
                            {
                                return SetDisconnectedStateCommand.Create(ex.ErrorInfo);
                            }

                            throw;
                        }
                    case SetFailedStateCommand cmd:

                        State.Connection.ClearKeyAndId();
                        AckProcessor.ClearQueueAndFailMessages(ErrorInfo.ReasonFailed);

                        var failedState = new ConnectionFailedState(ConnectionManager, cmd.Error, Logger);
                        SetState(failedState);

                        ConnectionManager.DestroyTransport(true);

                        break;
                    case SetDisconnectedStateCommand cmd:

                        if (cmd.ClearConnectionKey)
                        {
                            State.Connection.ClearKey();
                        }

                        var disconnectedState = new ConnectionDisconnectedState(ConnectionManager, cmd.Error, Logger)
                        {
                            RetryInstantly = cmd.RetryInstantly
                        };

                        SetState(disconnectedState, skipAttach: cmd.SkipAttach);

                        if (cmd.SkipAttach == false)
                        {
                            ConnectionManager.DestroyTransport(true);
                        }

                        if (cmd.RetryInstantly)
                        {
                            return SetConnectingStateCommand.Create();
                        }

                        break;

                    case SetClosingStateCommand cmd:
                        var transport = ConnectionManager.Transport;
                        var connectedTransport = transport?.State == TransportState.Connected;

                        var closingState = new ConnectionClosingState(ConnectionManager, connectedTransport, Logger);

                        SetState(closingState);

                        if (connectedTransport)
                        {
                            return SendMessageCommand.Create(new ProtocolMessage(ProtocolMessage.MessageAction.Close));
                        }
                        else
                        {
                            return SetClosedStateCommand.Create();
                        }

                        break;

                    case SetSuspendedStateCommand cmd:

                        if (cmd.ClearConnectionKey)
                        {
                            State.Connection.ClearKey();
                        }

                        AckProcessor.ClearQueueAndFailMessages(ErrorInfo.ReasonSuspended);

                        var suspendedState = new ConnectionSuspendedState(ConnectionManager, cmd.Error, Logger);
                        SetState(suspendedState);
                        break;

                    case SetClosedStateCommand cmd:

                        State.Connection.ClearKeyAndId();
                        AckProcessor.ClearQueueAndFailMessages(ErrorInfo.ReasonClosed);

                        var closedState = new ConnectionClosedState(ConnectionManager, cmd.Error, Logger)
                            {Exception = cmd.Exception};

                        SetState(closedState);

                        ConnectionManager.DestroyTransport(suppressClosedEvent: true);

                        break;
                }
            }
            catch (AblyException ex)
            {
                Logger.Error($"Error executing set state command {command.Name}", ex);

                if (command is SetFailedStateCommand == false)
                {
                    return SetFailedStateCommand.Create(ex.ErrorInfo);
                }
            }


            return EmptyCommand.Instance;
        }

        public void SetState(ConnectionStateBase newState, bool skipAttach = false)
        {
            if (Logger.IsDebug)
            {
                var message = $"Changing state from {State.Connection.State} => {newState.State}.";
                if (skipAttach) message += " SkipAttach";
                Logger.Debug(message);
            }

            try
            {
                if (newState.IsUpdate == false)
                {
                    if (State.Connection.State == newState.State)
                    {
                        if (Logger.IsDebug)
                        {
                            Logger.Debug($"State is already {State.Connection.State}. Skipping SetState action.");
                        }

                        return;
                    }

                    State.AttemptsInfo.UpdateAttemptState(newState);
                    State.Connection.CurrentStateObject.AbortTimer();
                }

                if (skipAttach == false)
                {
                    newState.OnAttachToContext();
                }
                else if (Logger.IsDebug)
                {
                    Logger.Debug($"xx {newState.State}: Skipping attaching.");
                }

                UpdateStateAndNotifyConnection(newState);
            }
            catch (AblyException ex)
            {
                Logger.Error("Error attaching to context", ex);

                UpdateStateAndNotifyConnection(newState);

                newState.AbortTimer();

                throw;
            }
        }

        private void UpdateStateAndNotifyConnection(ConnectionStateBase newState)
        {
            var change = State.Connection.UpdateState(newState, Logger);
            if (change != null)
            {
                Connection.NotifyUpdate(change);
            }
        }

        public void SendPendingMessages(bool resumed)
        {
            if (resumed)
            {
                // Resend any messages waiting an Ack Queue
                foreach (var message in AckProcessor.GetQueuedMessages())
                {
                    ConnectionManager.SendToTransport(message);
                }
            }

            if (Logger.IsDebug && State.PendingMessages.Count > 0)
            {
                Logger.Debug("Sending pending message: Count: " + State.PendingMessages.Count);
            }

            while (State.PendingMessages.Count > 0)
            {
                var queuedMessage = State.PendingMessages.Dequeue();
                SendMessage(queuedMessage.Message, queuedMessage.Callback);
            }
        }
    }
}
