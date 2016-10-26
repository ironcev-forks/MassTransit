﻿// Copyright 2007-2016 Chris Patterson, Dru Sellers, Travis Smith, et. al.
//  
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use
// this file except in compliance with the License. You may obtain a copy of the 
// License at 
// 
//     http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software distributed
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, either express or implied. See the License for the 
// specific language governing permissions and limitations under the License.
namespace MassTransit.Transports
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using GreenPipes;
    using Pipeline;
    using Util;


    public class ReceiveEndpointCollection :
        IReceiveEndpointCollection
    {
        readonly ConsumeObservable _consumeObservers;
        readonly Dictionary<string, IReceiveEndpoint> _endpoints;
        readonly Dictionary<string, HostReceiveEndpointHandle> _handles;
        readonly object _mutateLock = new object();
        readonly ReceiveEndpointObservable _receiveEndpointObservers;
        readonly ReceiveObservable _receiveObservers;

        public ReceiveEndpointCollection()
        {
            _endpoints = new Dictionary<string, IReceiveEndpoint>(StringComparer.OrdinalIgnoreCase);
            _handles = new Dictionary<string, HostReceiveEndpointHandle>(StringComparer.OrdinalIgnoreCase);
            _receiveObservers = new ReceiveObservable();
            _receiveEndpointObservers = new ReceiveEndpointObservable();
            _consumeObservers = new ConsumeObservable();
        }

        IEnumerator<IReceiveEndpoint> IEnumerable<IReceiveEndpoint>.GetEnumerator()
        {
            foreach (var endpoint in _endpoints.Values)
            {
                yield return endpoint;
            }
        }

        public IEnumerator GetEnumerator()
        {
            return _endpoints.GetEnumerator();
        }

        public void Add(string endpointName, IReceiveEndpoint endpoint)
        {
            if (endpoint == null)
                throw new ArgumentNullException(nameof(endpoint));
            if (string.IsNullOrWhiteSpace(endpointName))
                throw new ArgumentException($"The {nameof(endpointName)} must not be null or empty", nameof(endpointName));

            lock (_mutateLock)
            {
                if (_endpoints.ContainsKey(endpointName))
                    throw new ConfigurationException($"A receive endpoint with the same key was already added: {endpointName}");

                _endpoints.Add(endpointName, endpoint);
            }
        }

        public Task<HostReceiveEndpointHandle[]> StartEndpoints()
        {
            KeyValuePair<string, IReceiveEndpoint>[] startable;
            lock (_mutateLock)
                startable = _endpoints.Where(x => !_handles.ContainsKey(x.Key)).ToArray();

            return Task.WhenAll(startable.Select(x => StartEndpoint(x.Key, x.Value)));
        }

        public Task<HostReceiveEndpointHandle> Start(string endpointName)
        {
            if (string.IsNullOrWhiteSpace(endpointName))
                throw new ArgumentException($"The {nameof(endpointName)} must not be null or empty", nameof(endpointName));

            IReceiveEndpoint endpoint;
            lock (_mutateLock)
            {
                if (!_endpoints.TryGetValue(endpointName, out endpoint))
                    throw new ConfigurationException($"A receive endpoint with the same key was already added: {endpointName}");

                if(_handles.ContainsKey(endpointName))
                    throw new ArgumentException($"The specified endpoint has already been started: {endpointName}", nameof(endpointName));
            }

            return StartEndpoint(endpointName, endpoint);
        }

        public void Probe(ProbeContext context)
        {
            foreach (KeyValuePair<string, IReceiveEndpoint> receiveEndpoint in _endpoints)
            {
                var endpointScope = context.CreateScope("receiveEndpoint");
                endpointScope.Add("name", receiveEndpoint.Key);
                if (_handles.ContainsKey(receiveEndpoint.Key))
                {
                    endpointScope.Add("started", true);
                }
                receiveEndpoint.Value.Probe(endpointScope);
            }
        }

        public ConnectHandle ConnectReceiveObserver(IReceiveObserver observer)
        {
            return _receiveObservers.Connect(observer);
        }

        public ConnectHandle ConnectReceiveEndpointObserver(IReceiveEndpointObserver observer)
        {
            return _receiveEndpointObservers.Connect(observer);
        }

        public ConnectHandle ConnectConsumeMessageObserver<T>(IConsumeMessageObserver<T> observer) where T : class
        {
            return new MultipleConnectHandle(_endpoints.Values.Select(x => x.ConnectConsumeMessageObserver(observer)));
        }

        public ConnectHandle ConnectConsumeObserver(IConsumeObserver observer)
        {
            return _consumeObservers.Connect(observer);
        }

        async Task<HostReceiveEndpointHandle> StartEndpoint(string endpointName, IReceiveEndpoint endpoint)
        {
            try
            {
                var endpointReady = new ReceiveEndpointReadyObserver(endpoint);

                var consumeObserver = endpoint.ConnectConsumeObserver(_consumeObservers);
                var receiveObserver = endpoint.ConnectReceiveObserver(_receiveObservers);
                var receiveEndpointObserver = endpoint.ConnectReceiveEndpointObserver(_receiveEndpointObservers);
                var endpointHandle = endpoint.Start();

                var handle = new Handle(endpointHandle, receiveObserver, receiveEndpointObserver, consumeObserver, endpointReady.Ready,
                    () => Remove(endpointName), endpoint);

                await handle.Ready.ConfigureAwait(false);

                lock (_mutateLock)
                    _handles.Add(endpointName, handle);

                return handle;
            }
            catch
            {
                lock (_mutateLock)
                    _endpoints.Remove(endpointName);

                throw;
            }
        }

        void Remove(string endpointName)
        {
            if (string.IsNullOrWhiteSpace(endpointName))
                throw new ArgumentException($"The {nameof(endpointName)} must not be null or empty", nameof(endpointName));

            lock (_mutateLock)
            {
                _endpoints.Remove(endpointName);
                _handles.Remove(endpointName);
            }
        }


        class Handle :
            HostReceiveEndpointHandle
        {
            readonly ConnectHandle _consumeObserver;
            readonly ReceiveEndpointHandle _endpointHandle;
            readonly Action _onStopped;
            readonly ConnectHandle _receiveEndpointObserver;
            readonly ConnectHandle _receiveObserver;
            bool _stopped;

            public Handle(ReceiveEndpointHandle endpointHandle, ConnectHandle receiveObserver, ConnectHandle receiveEndpointObserver,
                ConnectHandle consumeObserver, Task<ReceiveEndpointReady> ready, Action onStopped, IReceiveEndpoint receiveEndpoint)
            {
                _endpointHandle = endpointHandle;
                _receiveObserver = receiveObserver;
                _receiveEndpointObserver = receiveEndpointObserver;
                _consumeObserver = consumeObserver;
                _onStopped = onStopped;
                ReceiveEndpoint = receiveEndpoint;

                Ready = ready;
            }

            public IReceiveEndpoint ReceiveEndpoint { get; }

            public Task<ReceiveEndpointReady> Ready { get; }

            public async Task StopAsync(CancellationToken cancellationToken)
            {
                if (_stopped)
                    return;

                _receiveObserver.Disconnect();
                _receiveEndpointObserver.Disconnect();
                _consumeObserver.Disconnect();

                await _endpointHandle.Stop(cancellationToken).ConfigureAwait(false);

                _onStopped();

                _stopped = true;
            }
        }


        class ReceiveEndpointReadyObserver
        {
            readonly Observer _observer;

            public ReceiveEndpointReadyObserver(IReceiveEndpoint receiveEndpoint)
            {
                _observer = new Observer(receiveEndpoint);
            }

            public Task<ReceiveEndpointReady> Ready => _observer.Ready;


            class Observer :
                IReceiveEndpointObserver
            {
                readonly ConnectHandle _handle;
                readonly TaskCompletionSource<ReceiveEndpointReady> _ready;

                public Observer(IReceiveEndpoint endpoint)
                {
                    _ready = new TaskCompletionSource<ReceiveEndpointReady>();
                    _handle = endpoint.ConnectReceiveEndpointObserver(this);
                }

                public Task<ReceiveEndpointReady> Ready => _ready.Task;

                Task IReceiveEndpointObserver.Ready(ReceiveEndpointReady ready)
                {
                    _ready.TrySetResult(ready);

                    _handle.Disconnect();

                    return TaskUtil.Completed;
                }

                Task IReceiveEndpointObserver.Completed(ReceiveEndpointCompleted completed)
                {
                    return TaskUtil.Completed;
                }

                public Task Faulted(ReceiveEndpointFaulted faulted)
                {
                    _ready.TrySetException(faulted.Exception);

                    _handle.Disconnect();

                    return TaskUtil.Completed;
                }
            }
        }
    }
}