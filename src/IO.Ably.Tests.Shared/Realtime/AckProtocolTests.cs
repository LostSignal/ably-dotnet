﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using IO.Ably.Realtime;
using IO.Ably.Realtime.Workflow;
using IO.Ably.Transport;
using IO.Ably.Types;
using Xunit;
using Xunit.Abstractions;

namespace IO.Ably.Tests.Realtime
{
    public class AckProtocolTests : AblySpecs
    {

        // TODO: Move to workflow tests for send message
//        [Theory]
//        [InlineData(ProtocolMessage.MessageAction.Message)]
//        [InlineData(ProtocolMessage.MessageAction.Presence)]
//        [Trait("spec", "RTN7a")]
//        [Trait("spec", "RTN7b")]
//        [Trait("sandboxTest", "needed")]
//        public void WhenSendingPresenceOrDataMessage_IncrementsMsgSerial(ProtocolMessage.MessageAction messageAction)
//        {
//            // Arrange
//            var ackProcessor = GetAckProcessor();
//            var targetMessage1 = new ProtocolMessage(messageAction, "Test");
//            var targetMessage2 = new ProtocolMessage(messageAction, "Test");
//            var targetMessage3 = new ProtocolMessage(messageAction, "Test");
//
//            // Act
//            ackProcessor.QueueIfNecessary(targetMessage1, null);
//            ackProcessor.QueueIfNecessary(targetMessage2, null);
//            ackProcessor.QueueIfNecessary(targetMessage3, null);
//
//            // Assert
//            Assert.Equal(0, targetMessage1.MsgSerial);
//            Assert.Equal(1, targetMessage2.MsgSerial);
//            Assert.Equal(2, targetMessage3.MsgSerial);
//        }


        // TODO: Move the test to the workflow tests for send message

//        [Theory]
//        [InlineData(ProtocolMessage.MessageAction.Ack)]
//        [InlineData(ProtocolMessage.MessageAction.Attach)]
//        [InlineData(ProtocolMessage.MessageAction.Attached)]
//        [InlineData(ProtocolMessage.MessageAction.Close)]
//        [InlineData(ProtocolMessage.MessageAction.Closed)]
//        [InlineData(ProtocolMessage.MessageAction.Connect)]
//        [InlineData(ProtocolMessage.MessageAction.Connected)]
//        [InlineData(ProtocolMessage.MessageAction.Detach)]
//        [InlineData(ProtocolMessage.MessageAction.Detached)]
//        [InlineData(ProtocolMessage.MessageAction.Disconnect)]
//        [InlineData(ProtocolMessage.MessageAction.Disconnected)]
//        [InlineData(ProtocolMessage.MessageAction.Error)]
//        [InlineData(ProtocolMessage.MessageAction.Heartbeat)]
//        [InlineData(ProtocolMessage.MessageAction.Nack)]
//        [InlineData(ProtocolMessage.MessageAction.Sync)]
//        [Trait("spec", "RTN7a")]
//        public void WhenSendingNotAPresenceOrDataMessage_MsgSerialNotIncremented(ProtocolMessage.MessageAction messageAction)
//        {
//            // Arrange
//            var ackProcessor = GetAckProcessor();
//            var targetMessage1 = new ProtocolMessage(messageAction, "Test");
//            var targetMessage2 = new ProtocolMessage(messageAction, "Test");
//            var targetMessage3 = new ProtocolMessage(messageAction, "Test");
//
//            // Act
//            ackProcessor.QueueIfNecessary(targetMessage1, null);
//            ackProcessor.QueueIfNecessary(targetMessage2, null);
//            ackProcessor.QueueIfNecessary(targetMessage3, null);
//
//            // Assert
//            Assert.Equal(0, targetMessage1.MsgSerial);
//            Assert.Equal(0, targetMessage2.MsgSerial);
//            Assert.Equal(0, targetMessage3.MsgSerial);
//        }

        [Theory]
        [InlineData(ProtocolMessage.MessageAction.Ack)]
        [InlineData(ProtocolMessage.MessageAction.Nack)]
        public async Task WhenReceivingAckOrNackMessage_ShouldHandleAction(ProtocolMessage.MessageAction action)
        {
            // Act
            bool result = await GetAckProcessor().OnMessageReceived(new ProtocolMessage(action), null);

            // Assert
            Assert.True(result);
        }

        [Theory]
        [InlineData(ProtocolMessage.MessageAction.Attach)]
        [InlineData(ProtocolMessage.MessageAction.Attached)]
        [InlineData(ProtocolMessage.MessageAction.Close)]
        [InlineData(ProtocolMessage.MessageAction.Closed)]
        [InlineData(ProtocolMessage.MessageAction.Connect)]
        [InlineData(ProtocolMessage.MessageAction.Connected)]
        [InlineData(ProtocolMessage.MessageAction.Detach)]
        [InlineData(ProtocolMessage.MessageAction.Detached)]
        [InlineData(ProtocolMessage.MessageAction.Disconnect)]
        [InlineData(ProtocolMessage.MessageAction.Disconnected)]
        [InlineData(ProtocolMessage.MessageAction.Error)]
        [InlineData(ProtocolMessage.MessageAction.Heartbeat)]
        [InlineData(ProtocolMessage.MessageAction.Message)]
        [InlineData(ProtocolMessage.MessageAction.Presence)]
        [InlineData(ProtocolMessage.MessageAction.Sync)]
        public async Task WhenReceivingNonAckOrNackMessage_ShouldNotHandleAction(ProtocolMessage.MessageAction action)
        {
            // Act
            bool result = await GetAckProcessor().OnMessageReceived(new ProtocolMessage(action), null);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public async Task OnAckReceivedForAMessage_AckCallbackCalled()
        {
            // Arrange
            var ackProcessor = GetAckProcessor();
            var callbacks = new List<Tuple<bool, ErrorInfo>>();
            var message = new ProtocolMessage(ProtocolMessage.MessageAction.Message, "Test");
            Action<bool, ErrorInfo> callback = (ack, err) =>
            {
                callbacks.Add(Tuple.Create(ack, err));
            };

            // Act
            ackProcessor.Queue(message, callback);
            await ackProcessor.OnMessageReceived(new ProtocolMessage(ProtocolMessage.MessageAction.Ack) { MsgSerial = 0, Count = 1 }, null);
            ackProcessor.Queue(message, callback);
            await ackProcessor.OnMessageReceived(new ProtocolMessage(ProtocolMessage.MessageAction.Ack) { MsgSerial = 1, Count = 1 }, null);

            // Assert
            Assert.Equal(2, callbacks.Count);
            Assert.True(callbacks.TrueForAll(c => c.Item1)); // Ack
            Assert.True(callbacks.TrueForAll(c => c.Item2 == null)); // No error
        }

        [Fact]
        public async Task WhenSendingMessage_AckCallbackCalled_ForMultipleMessages()
        {
            // Arrange
            var ackProcessor = GetAckProcessor();
            var connection = new Connection(new AblyRealtime(ValidKey), () => DateTimeOffset.UtcNow);
            List<Tuple<bool, ErrorInfo>> callbacks = new List<Tuple<bool, ErrorInfo>>();

            // Act
            ackProcessor.Queue(new ProtocolMessage(ProtocolMessage.MessageAction.Message, "Test"), (ack, err) => { if (callbacks.Count == 0) { callbacks.Add(Tuple.Create(ack, err)); } });
            ackProcessor.Queue(new ProtocolMessage(ProtocolMessage.MessageAction.Message, "Test"), (ack, err) => { if (callbacks.Count == 1) { callbacks.Add(Tuple.Create(ack, err)); } });
            ackProcessor.Queue(new ProtocolMessage(ProtocolMessage.MessageAction.Message, "Test"), (ack, err) => { if (callbacks.Count == 2) { callbacks.Add(Tuple.Create(ack, err)); } });
            await ackProcessor.OnMessageReceived(new ProtocolMessage(ProtocolMessage.MessageAction.Ack) { MsgSerial = 0, Count = 3 }, new RealtimeState());

            // Assert
            Assert.Equal(3, callbacks.Count);
            Assert.True(callbacks.TrueForAll(c => c.Item1)); // Ack
            Assert.True(callbacks.TrueForAll(c => c.Item2 == null)); // No error
        }

        [Fact]
        public async Task WithNackMessageReceived_CallbackIsCalledWithError()
        {
            // Arrange
            var ackProcessor = GetAckProcessor();
            var callbacks = new List<Tuple<bool, ErrorInfo>>();
            var message = new ProtocolMessage(ProtocolMessage.MessageAction.Message, "Test");
            Action<bool, ErrorInfo> callback = (ack, err) => { callbacks.Add(Tuple.Create(ack, err)); };

            // Act
            ackProcessor.Queue(message, callback);
            await ackProcessor.OnMessageReceived(new ProtocolMessage(ProtocolMessage.MessageAction.Nack) { MsgSerial = 0, Count = 1 }, null);

            ackProcessor.Queue(message, callback);
            await ackProcessor.OnMessageReceived(new ProtocolMessage(ProtocolMessage.MessageAction.Nack) { MsgSerial = 1, Count = 1 }, null);

            // Assert
            Assert.Equal(2, callbacks.Count);
            Assert.True(callbacks.TrueForAll(c => c.Item1 == false)); // Nack
            Assert.True(callbacks.TrueForAll(c => c.Item2 != null)); // Error
        }

        [Fact]
        public async Task WhenNackReceivedForMultipleMessage_AllCallbacksAreCalledAndErrorMessagePassed()
        {
            // Arrange
            var ackProcessor = GetAckProcessor();
            var callbacks = new List<Tuple<bool, ErrorInfo>>();
            var message = new ProtocolMessage(ProtocolMessage.MessageAction.Message, "Test");
            Action<bool, ErrorInfo> callback = (ack, err) => { callbacks.Add(Tuple.Create(ack, err)); };
            ErrorInfo error = new ErrorInfo("reason", 123);

            // Act
            ackProcessor.Queue(message, callback);
            ackProcessor.Queue(message, callback);
            ackProcessor.Queue(message, callback);

            await ackProcessor.OnMessageReceived(new ProtocolMessage(ProtocolMessage.MessageAction.Nack) { MsgSerial = 0, Count = 3, Error = error }, null);

            // Assert
            Assert.Equal(3, callbacks.Count);
            Assert.True(callbacks.TrueForAll(c => !c.Item1)); // Nack
            Assert.True(callbacks.TrueForAll(c => ReferenceEquals(c.Item2, error))); // Error
        }

        public AckProtocolTests(ITestOutputHelper output)
            : base(output)
        {
            GetAckProcessor();
        }

        private AcknowledgementProcessor GetAckProcessor()
        {
            var connection = new Connection(new AblyRealtime(ValidKey), TestHelpers.NowFunc());
            connection.Initialise();
            return new AcknowledgementProcessor(connection, new RealtimeState.ConnectionData(null));
        }
    }
}
