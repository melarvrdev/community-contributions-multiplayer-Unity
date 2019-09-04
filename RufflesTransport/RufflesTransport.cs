﻿using System;
using System.Collections.Generic;
using System.Net;
using MLAPI.Transports;
using MLAPI.Transports.Tasks;
using Ruffles.Configuration;
using Ruffles.Core;
using UnityEngine;

namespace RufflesTransport
{
    public class RufflesTransport : Transport
    {
        [Serializable]
        public class RufflesChannel
        {
            public string Name;
            public Ruffles.Channeling.ChannelType Type;
        }

        public override bool IsSupported => Application.platform != RuntimePlatform.WebGLPlayer;

        // Inspector / settings
        public int MessageBufferSize = 1024 * 5;
        public int MaxConnections = 100;
        public string ConnectAddress = "127.0.0.1";
        public int ConnectPort = 7777;
        public int ServerListenPort = 7777;
        public List<RufflesChannel> Channels = new List<RufflesChannel>();

        // Runtime / state
        private byte[] messageBuffer;
        private WeakReference temporaryBufferReference;
        private bool isConnector = false;

        // Lookup / translation
        private readonly Dictionary<ulong, Ruffles.Connections.Connection> connections = new Dictionary<ulong, Ruffles.Connections.Connection>();
        private readonly Dictionary<string, byte> channelNameToId = new Dictionary<string, byte>();
        private readonly Dictionary<byte, string> channelIdToName = new Dictionary<byte, string>();
        private Ruffles.Connections.Connection serverConnection;

        // Ruffles
        private RuffleSocket socket;

        public override ulong ServerClientId => GetMLAPIClientId(0, true);

        public override void Send(ulong clientId, ArraySegment<byte> data, string channelName)
        {
            GetRufflesConnectionDetails(clientId, out ulong connectionId);

            byte channelId = channelNameToId[channelName];

            socket.Send(data, connectionId, channelId, false);
        }

        public override NetEventType PollEvent(out ulong clientId, out string channelName, out ArraySegment<byte> payload, out float receiveTime)
        {
            socket.RunInternalLoop();
            NetworkEvent @event = socket.Poll();

            receiveTime = Time.realtimeSinceStartup - (float)(DateTime.Now - @event.SocketReceiveTime).TotalSeconds;

            if (@event.Type != NetworkEventType.Nothing)
            {
                clientId = GetMLAPIClientId(@event.Connection.Id, false);
            }
            else
            {
                clientId = 0;
            }

            byte[] dataBuffer = messageBuffer;

            if (@event.Type == NetworkEventType.Data)
            {
                if (@event.Data.Count > messageBuffer.Length)
                {
                    if (temporaryBufferReference != null && temporaryBufferReference.IsAlive && ((byte[])temporaryBufferReference.Target).Length >= @event.Data.Count)
                    {
                        dataBuffer = (byte[])temporaryBufferReference.Target;
                    }
                    else
                    {
                        dataBuffer = new byte[@event.Data.Count];
                        temporaryBufferReference = new WeakReference(dataBuffer);
                    }
                }

                Buffer.BlockCopy(@event.Data.Array, @event.Data.Offset, dataBuffer, 0, @event.Data.Count);
                payload = new ArraySegment<byte>(dataBuffer, 0, @event.Data.Count);

                @event.Recycle();
            }
            else
            {
                payload = new ArraySegment<byte>();
            }

            channelName = channelIdToName[@event.ChannelId];

            // Translate NetworkEventType to NetEventType
            switch (@event.Type)
            {
                case NetworkEventType.Data:
                    return NetEventType.Data;
                case NetworkEventType.Connect:
                    {
                        connections.Add(@event.Connection.Id, @event.Connection);

                        // Set the server connectionId
                        if (isConnector)
                        {
                            serverConnection = @event.Connection;
                        }

                        return NetEventType.Connect;
                    }
                case NetworkEventType.Timeout:
                case NetworkEventType.Disconnect:
                    {
                        if (@event.Connection == serverConnection)
                            serverConnection = null;

                        connections.Remove(@event.Connection.Id);

                        @event.Connection.Recycle();

                        return NetEventType.Disconnect;
                    }
                case NetworkEventType.Nothing:
                    return NetEventType.Nothing;
            }

            return NetEventType.Nothing;
        }

        public override SocketTasks StartClient()
        {
            SocketConfig config = GetConfig();
            // The OS will grab a port
            config.DualListenPort = 0;
            socket = new RuffleSocket(config);

            isConnector = true;
            socket.Connect(new IPEndPoint(IPAddress.Parse(ConnectAddress), ConnectPort));

            return SocketTask.Done.AsTasks();
        }

        public override SocketTasks StartServer()
        {
            SocketConfig config = GetConfig();
            config.DualListenPort = (ushort)ServerListenPort;

            socket = new RuffleSocket(config);

            return SocketTask.Done.AsTasks();
        }

        public override void DisconnectRemoteClient(ulong clientId)
        {
            GetRufflesConnectionDetails(clientId, out ulong connectionId);
            socket.Disconnect(connections[connectionId], true);
        }

        public override void DisconnectLocalClient()
        {
            socket.Disconnect(serverConnection, true);
        }

        public override ulong GetCurrentRtt(ulong clientId)
        {
            GetRufflesConnectionDetails(clientId, out ulong connectionId);
            return (ulong)connections[connectionId].Roundtrip;
        }

        public override void Shutdown()
        {
            channelIdToName.Clear();
            channelNameToId.Clear();
            connections.Clear();
        }

        public override void Init()
        {
            messageBuffer = new byte[MessageBufferSize];
        }

        public ulong GetMLAPIClientId(ulong connectionId, bool isServer)
        {
            if (isServer)
            {
                return 0;
            }
            else
            {
                return connectionId + 1;
            }
        }

        public void GetRufflesConnectionDetails(ulong clientId, out ulong connectionId)
        {
            if (clientId == 0)
            {
                connectionId = serverConnection.Id;
            }
            else
            {
                connectionId = (ushort)(clientId - 1);
            }
        }

        private SocketConfig GetConfig()
        {
            SocketConfig config = new SocketConfig();

            int channelCount = MLAPI_CHANNELS.Length + Channels.Count;
            config.ChannelTypes = new Ruffles.Channeling.ChannelType[channelCount];

            for (byte i = 0; i < MLAPI_CHANNELS.Length; i++)
            {
                config.ChannelTypes[i] = ConvertChannelType(MLAPI_CHANNELS[i].Type);
                channelIdToName.Add(i, MLAPI_CHANNELS[i].Name);
                channelNameToId.Add(MLAPI_CHANNELS[i].Name, i);
            }

            for (byte i = (byte)MLAPI_CHANNELS.Length; i < Channels.Count + MLAPI_CHANNELS.Length; i++)
            {
                config.ChannelTypes[i] = Channels[i].Type;
                channelIdToName.Add(i, Channels[i].Name);
                channelNameToId.Add(Channels[i].Name, i);
            }

            return config;
        }

        private Ruffles.Channeling.ChannelType ConvertChannelType(ChannelType type)
        {
            switch (type)
            {
                case ChannelType.Reliable:
                    return Ruffles.Channeling.ChannelType.Reliable;
                case ChannelType.ReliableFragmentedSequenced:
                    return Ruffles.Channeling.ChannelType.ReliableSequenced;
                case ChannelType.ReliableSequenced:
                    return Ruffles.Channeling.ChannelType.ReliableSequenced;
                case ChannelType.Unreliable:
                    return Ruffles.Channeling.ChannelType.Unreliable;
                case ChannelType.UnreliableSequenced:
                    return Ruffles.Channeling.ChannelType.UnreliableSequenced;
            }

            return Ruffles.Channeling.ChannelType.Reliable;
        }
    }
}
