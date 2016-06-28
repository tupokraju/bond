﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Bond.Comm.Epoxy
{
    using System;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;
    using Bond.Comm.Service;

    public class EpoxyTransportBuilder : TransportBuilder<EpoxyTransport>
    {
        public override EpoxyTransport Construct()
        {
            return new EpoxyTransport(LayerStackProvider, LogSink, EnableDebugLogs, MetricsSink);
        }
    }

    public class EpoxyTransport : Transport<EpoxyConnection, EpoxyListener>
    {
        public const int DefaultPort = 25188;

        readonly ILayerStackProvider layerStackProvider;
        readonly Logger logger;
        readonly Metrics metrics;

        public struct Endpoint
        {
            public readonly string Host;
            public readonly int Port;

            public Endpoint(string host, int port)
            {
                Host = host;
                Port = port;
            }

            public Endpoint(IPEndPoint ipEndPoint) : this(ipEndPoint.Address.ToString(), ipEndPoint.Port) { }

            public override string ToString()
            {
                return new UriBuilder("epoxy", Host, Port).Uri.ToString();
            }
        }

        public EpoxyTransport(
            ILayerStackProvider layerStackProvider,
            ILogSink logSink, bool enableDebugLogs,
            IMetricsSink metricsSink)
        {
            // Layer stack provider may be null
            this.layerStackProvider = layerStackProvider;
            // Log sink may be null
            logger = new Logger(logSink, enableDebugLogs);
            // Metrics sink may be null
            metrics = new Metrics(metricsSink);
        }

        public override Error GetLayerStack(out ILayerStack stack)
        {
            if (layerStackProvider != null)
            {
                return layerStackProvider.GetLayerStack(out stack);
            }
            else
            {
                stack = null;
                return null;
            }
        }

        public static Endpoint? Parse(string address, Logger logger)
        {
            Uri uri;
            try
            {
                uri = new Uri(address);
            }
            catch (Exception ex) when (ex is UriFormatException || ex is ArgumentNullException)
            {
                logger.Site().Error(ex, "given {0}, but URI parsing threw {1}", address, ex.Message);
                return null;
            }

            switch (uri.Scheme)
            {
                case "epoxy":
                    break;
                default:
                    logger.Site().Error("given {0}, but URI scheme must be epoxy://", address);
                    return null;
            }

            var port = uri.Port != -1 ? uri.Port : DefaultPort;

            if (uri.PathAndQuery != "/")
            {
                logger.Site().Error("given {0}, but Epoxy does not accept a path/resource", address);
                return null;
            }

            return new Endpoint(uri.Host, port);
        }

        public override async Task<EpoxyConnection> ConnectToAsync(string address, CancellationToken ct)
        {
            var endpoint = Parse(address, logger);
            if (endpoint == null)
            {
                throw new ArgumentException(address + " was not a valid Epoxy URI", nameof(address));
            }

            return await ConnectToAsync(endpoint.Value, ct);
        }

        public async Task<EpoxyConnection> ConnectToAsync(Endpoint endpoint, CancellationToken ct)
        {
            logger.Site().Information("Connecting to {0}.", endpoint);
            var socket = MakeClientSocket();
            await Task.Factory.FromAsync(
                socket.BeginConnect, socket.EndConnect, endpoint.Host, endpoint.Port, state: null);

            // TODO: keep these in some master collection for shutdown
            var connection = EpoxyConnection.MakeClientConnection(this, socket, logger, metrics);
            await connection.StartAsync();
            return connection;
        }

        public async Task<EpoxyConnection> ConnectToAsync(Endpoint endpoint)
        {
            return await ConnectToAsync(endpoint, CancellationToken.None);
        }

        public override EpoxyListener MakeListener(string address)
        {
            return MakeListener(ParseStringAddress(address));
        }

        public EpoxyListener MakeListener(IPEndPoint address)
        {
            return new EpoxyListener(this, address, logger, metrics);
        }

        public override Task StopAsync()
        {
            return TaskExt.CompletedTask;
        }

        public static IPEndPoint ParseStringAddress(string address)
        {
            if (string.IsNullOrEmpty(address))
            {
                throw new ArgumentException("Address cannot be null or empty", nameof(address));
            }

            int portStartIndex = address.IndexOf(':');

            string ipAddressPart;

            if (portStartIndex == -1)
            {
                ipAddressPart = address;
            }
            else
            {
                ipAddressPart = address.Substring(0, portStartIndex);
            }

            IPAddress ipAddr;
            if (!IPAddress.TryParse(ipAddressPart, out ipAddr))
            {
                throw new ArgumentException("Couldn't parse IP address from \"" + address + "\"", nameof(address));
            }

            int port;
            if (portStartIndex == -1)
            {
                port = DefaultPort;
            }
            else
            {
                string portPart = address.Substring(portStartIndex + 1);
                if (!int.TryParse(portPart, out port))
                {
                    throw new ArgumentException("Couldn't parse port from \"" + address + "\"", nameof(address));
                }
            }

            return new IPEndPoint(ipAddr, port);
        }

        private Socket MakeClientSocket()
        {
            return new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }
    }
}
