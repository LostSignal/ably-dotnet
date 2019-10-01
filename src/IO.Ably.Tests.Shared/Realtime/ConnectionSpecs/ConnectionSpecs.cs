﻿using System;
using System.Threading.Tasks;
using IO.Ably.Realtime;
using IO.Ably.Types;
using Xunit.Abstractions;

namespace IO.Ably.Tests.Realtime
{
    public class ConnectionSpecsBase : AblyRealtimeSpecs
    {
        public const string TestChannelName = "test";

        protected FakeTransportFactory FakeTransportFactory { get; private set; }

        protected FakeTransport LastCreatedTransport => FakeTransportFactory.LastCreatedTransport;

        internal AblyRealtime GetClientWithFakeTransport(Action<ClientOptions> optionsAction = null, Func<AblyRequest, Task<AblyResponse>> handleRequestFunc = null)
        {
            var options = new ClientOptions(ValidKey) { TransportFactory = FakeTransportFactory };
            optionsAction?.Invoke(options);
            var client = GetRealtimeClient(options, handleRequestFunc);
            return client;
        }

        internal async Task<AblyRealtime> GetConnectedClient(Action<ClientOptions> optionsAction = null, Func<AblyRequest, Task<AblyResponse>> handleRequestFunc = null)
        {
            var client = GetClientWithFakeTransport(optionsAction, handleRequestFunc);
            client.FakeProtocolMessageReceived(ConnectedProtocolMessage);
            await client.WaitForState(ConnectionState.Connected);
            return client;
        }

        protected ProtocolMessage ConnectedProtocolMessage =>
            new ProtocolMessage(ProtocolMessage.MessageAction.Connected)
            {
                ConnectionDetails = new ConnectionDetails() {ConnectionKey = "connectionKey"},
                ConnectionId = "1",
                ConnectionSerial = 100
            };

        protected Task<IRealtimeChannel> GetChannel(Action<ClientOptions> optionsAction = null) => GetConnectedClient(optionsAction).MapAsync(client => client.Channels.Get("test"));

        protected Task<(AblyRealtime, IRealtimeChannel)> GetClientAndChannel(Action<ClientOptions> optionsAction = null) =>
            GetConnectedClient(optionsAction).MapAsync(x => (x, x.Channels.Get("test")));

        protected Task<IRealtimeChannel> GetTestChannel(IRealtimeClient client = null, Action<ClientOptions> optionsAction = null)
        {
            if (client == null)
            {
                return GetConnectedClient().MapAsync(x => x.Channels.Get(TestChannelName));
            }

            return Task.FromResult(client.Channels.Get(TestChannelName));
        }

        public ConnectionSpecsBase(ITestOutputHelper output)
            : base(output)
        {
            FakeTransportFactory = new FakeTransportFactory();
        }
    }
}
