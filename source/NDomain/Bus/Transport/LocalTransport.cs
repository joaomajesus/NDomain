﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NDomain.Bus.Transport
{
    public class LocalTransportFactory : ITransportFactory
    {
        // shared bus, for all local clients
        readonly LocalBus bus;

        public LocalTransportFactory()
        {
            this.bus = new LocalBus();
        }

        public IInboundTransport CreateInboundTransport(string endpoint)
        {
            var queue = this.bus.GetEndpointQueue(endpoint);

            return new LocalInboundTransport(queue);
        }

        public IOutboundTransport CreateOutboundTransport()
        {
            return new LocalOutboundTransport(bus);
        }
    }

    class LocalBus
    {
        readonly ConcurrentDictionary<string, BlockingCollection<LocalMessage>> bus;

        public LocalBus()
        {
            this.bus = new ConcurrentDictionary<string, BlockingCollection<LocalMessage>>();
        }

        public BlockingCollection<LocalMessage> GetEndpointQueue(string endpoint)
        {
            var queue = this.bus.GetOrAdd(endpoint, e => new BlockingCollection<LocalMessage>(
                                                                new ConcurrentQueue<LocalMessage>()));

            return queue;
        }
    }

    class LocalInboundTransport : IInboundTransport
    {
        readonly BlockingCollection<LocalMessage> queue;

        public LocalInboundTransport(BlockingCollection<LocalMessage> queue)
        {
            this.queue = queue;
        }

        public Task<IMessageTransaction> Receive(TimeSpan? timeout = null)
        {
            return Task.Factory.StartNew(() =>
            {
                LocalMessage message;
                if (!this.queue.TryTake(out message, timeout ?? TimeSpan.FromSeconds(60)))
                {
                    return null;
                }

                IMessageTransaction transaction = new LocalMessageTransaction(
                                                        message,
                                                        () => { }, // nothing to do on commit
                                                        () =>
                                                        {
                                                            message.Failed(); // notify failure, increases retry count
                                                            this.queue.Add(message); // add to the queue again
                                                        });
                return transaction;
            });
        }
    }

    class LocalOutboundTransport : IOutboundTransport
    {
        readonly LocalBus bus;

        public LocalOutboundTransport(LocalBus bus)
        {
            this.bus = bus;
        }

        public Task Send(TransportMessage message)
        {
            var endpoint = message.Headers[MessageHeaders.Endpoint];
            var endpointQueue = this.bus.GetEndpointQueue(endpoint);

            endpointQueue.Add(new LocalMessage(message));
            return Task.FromResult(true);
        }

        public Task SendMultiple(IEnumerable<TransportMessage> messages)
        {
            foreach (var message in messages)
            {
                var endpoint = message.Headers[MessageHeaders.Endpoint];
                var endpointQueue = this.bus.GetEndpointQueue(endpoint);

                endpointQueue.Add(new LocalMessage(message));
            }
            return Task.FromResult(true);
        }
    }

    class LocalMessage
    {
        readonly TransportMessage message;
        int retryCount;

        public LocalMessage(TransportMessage message)
        {
            this.message = message;
            this.retryCount = 0;
        }

        public TransportMessage Message { get { return this.message; } }
        public int RetryCount { get { return Thread.VolatileRead(ref retryCount); } }

        public void Failed()
        {
            Interlocked.Increment(ref retryCount);
        }
    }

    class LocalMessageTransaction : IMessageTransaction
    {
        readonly Action onCommit;
        readonly Action onFail;

        readonly LocalMessage localMsg;

        public LocalMessageTransaction(LocalMessage message, Action onCommit, Action onFail)
        {
            this.localMsg = message;

            this.onCommit = onCommit;
            this.onFail = onFail;
        }

        public TransportMessage Message { get { return this.localMsg.Message; } }
        public int RetryCount { get { return this.localMsg.RetryCount; } }

        public Task Commit()
        {
            this.onCommit();
            return Task.FromResult(true);
        }

        public Task Fail()
        {
            this.onFail();
            return Task.FromResult(true);
        }
    }
}
