﻿using System;
using System.Collections.Generic;
using ENet;
using MLAPI.Transports;
using MLAPI.Transports.Tasks;

namespace EnetTransport
{
    public class EnetTransport : Transport
    {
        [Serializable]
        public struct EnetChannel
        {
            [UnityEngine.HideInInspector]
            public byte Id;
            public string Name;
            public EnetDelivery Flags;
        }

        public enum EnetDelivery
        {
            UnreliableSequenced,
            ReliableSequenced,
            Unreliable
        }

        public override bool IsSupported => UnityEngine.Application.platform != UnityEngine.RuntimePlatform.WebGLPlayer;

        public ushort Port = 7777;
        public string Address = "127.0.0.1";
        public int MaxClients = 100;
        public List<EnetChannel> Channels = new List<EnetChannel>();
        public int MessageBufferSize = 1024 * 5;

        [UnityEngine.Header("ENET Settings")]
        public uint PingInterval = 500;
        public uint TimeoutLimit = 32;
        public uint TimeoutMinimum = 5000;
        public uint TimeoutMaximum = 30000;


        // Runtime / state
        private byte[] messageBuffer;
        private WeakReference temporaryBufferReference;


        private readonly Dictionary<uint, Peer> connectedEnetPeers = new Dictionary<uint, Peer>();

        private readonly Dictionary<string, byte> channelNameToId = new Dictionary<string, byte>();
        private readonly Dictionary<byte, string> channelIdToName = new Dictionary<byte, string>();
        private readonly Dictionary<byte, EnetChannel> internalChannels = new Dictionary<byte, EnetChannel>();

        private Host host;

        private uint serverPeerId;

        private SocketTask connectTask;

        public override ulong ServerClientId => GetMLAPIClientId(0, true);

        public override void Send(ulong clientId, ArraySegment<byte> data, string channelName)
        {
            Packet packet = default(Packet);

            packet.Create(data.Array, data.Offset, data.Count, PacketFlagFromDelivery(internalChannels[channelNameToId[channelName]].Flags));

            GetEnetConnectionDetails(clientId, out uint peerId);

            connectedEnetPeers[peerId].Send(channelNameToId[channelName], ref packet);
        }

        public override NetEventType PollEvent(out ulong clientId, out string channelName, out ArraySegment<byte> payload, out float receiveTime)
        {
            Event @event;

            if (host.CheckEvents(out @event) <= 0)
            {
                if (host.Service(0, out @event) <= 0)
                {
                    clientId = 0;
                    channelName = null;
                    payload = new ArraySegment<byte>();
                    receiveTime = UnityEngine.Time.realtimeSinceStartup;

                    return NetEventType.Nothing;
                }
            }

            clientId = GetMLAPIClientId(@event.Peer.ID, false);

            switch (@event.Type)
            {
                case EventType.None:
                    {
                        channelName = null;
                        payload = new ArraySegment<byte>();
                        receiveTime = UnityEngine.Time.realtimeSinceStartup;

                        return NetEventType.Nothing;
                    }
                case EventType.Connect:
                    {
                        channelName = null;
                        payload = new ArraySegment<byte>();
                        receiveTime = UnityEngine.Time.realtimeSinceStartup;

                        connectedEnetPeers.Add(@event.Peer.ID, @event.Peer);

                        @event.Peer.PingInterval(PingInterval);
                        @event.Peer.Timeout(TimeoutLimit, TimeoutMinimum, TimeoutMaximum);

                        if (connectTask != null)
                        {
                            connectTask.Success = true;
                            connectTask.IsDone = true;
                            connectTask = null;
                        }

                        return NetEventType.Connect;
                    }
                case EventType.Disconnect:
                    {
                        channelName = null;
                        payload = new ArraySegment<byte>();
                        receiveTime = UnityEngine.Time.realtimeSinceStartup;

                        connectedEnetPeers.Remove(@event.Peer.ID);

                        if (connectTask != null)
                        {
                            connectTask.Success = false;
                            connectTask.IsDone = true;
                            connectTask = null;
                        }

                        return NetEventType.Disconnect;
                    }
                case EventType.Receive:
                    {
                        channelName = channelIdToName[@event.ChannelID];
                        receiveTime = UnityEngine.Time.realtimeSinceStartup;
                        int size = @event.Packet.Length;

                        if (size > messageBuffer.Length)
                        {
                            byte[] tempBuffer;

                            if (temporaryBufferReference != null && temporaryBufferReference.IsAlive && ((byte[])temporaryBufferReference.Target).Length >= size)
                            {
                                tempBuffer = (byte[])temporaryBufferReference.Target;
                            }
                            else
                            {
                                tempBuffer = new byte[size];
                                temporaryBufferReference = new WeakReference(tempBuffer);
                            }

                            @event.Packet.CopyTo(tempBuffer);
                            payload = new ArraySegment<byte>(tempBuffer, 0, size);
                        }
                        else
                        {
                            @event.Packet.CopyTo(messageBuffer);
                            payload = new ArraySegment<byte>(messageBuffer, 0, size);
                        }

                        @event.Packet.Dispose();

                        return NetEventType.Data;
                    }
                case EventType.Timeout:
                    {
                        channelName = null;
                        payload = new ArraySegment<byte>();
                        receiveTime = UnityEngine.Time.realtimeSinceStartup;

                        connectedEnetPeers.Remove(@event.Peer.ID);

                        return NetEventType.Disconnect;
                    }
                default:
                    {
                        channelName = null;
                        payload = new ArraySegment<byte>();
                        receiveTime = UnityEngine.Time.realtimeSinceStartup;

                        return NetEventType.Nothing;
                    }
            }
        }

        public override SocketTasks StartClient()
        {
            SocketTask task = SocketTask.Working;

            host = new Host();

            host.Create(1, MLAPI_CHANNELS.Length + Channels.Count);

            Address address = new Address();
            address.Port = Port;
            address.SetHost(Address);

            Peer serverPeer = host.Connect(address, MLAPI_CHANNELS.Length + Channels.Count);

            serverPeer.PingInterval(PingInterval);
            serverPeer.Timeout(TimeoutLimit, TimeoutMinimum, TimeoutMaximum);

            serverPeerId = serverPeer.ID;

            connectTask = task;

            return task.AsTasks();
        }

        public override SocketTasks StartServer()
        {
            host = new Host();

            Address address = new Address();
            address.Port = Port;

            host.Create(address, MaxClients, MLAPI_CHANNELS.Length + Channels.Count);

            return SocketTask.Done.AsTasks();
        }

        public override void DisconnectRemoteClient(ulong clientId)
        {
            GetEnetConnectionDetails(serverPeerId, out uint peerId);

            connectedEnetPeers[peerId].DisconnectNow(0);
        }

        public override void DisconnectLocalClient()
        {
            host.Flush();

            GetEnetConnectionDetails(serverPeerId, out uint peerId);

            if (connectedEnetPeers.ContainsKey(peerId))
            {
                connectedEnetPeers[peerId].DisconnectNow(0);
            }
        }

        public override ulong GetCurrentRtt(ulong clientId)
        {
            GetEnetConnectionDetails(clientId, out uint peerId);

            return connectedEnetPeers[peerId].RoundTripTime;
        }

        public override void Shutdown()
        {
            if (host != null)
            {
                host.Flush();
                host.Dispose();
            }

            Library.Deinitialize();
        }

        public override void Init()
        {
            Library.Initialize();

            internalChannels.Clear();
            channelIdToName.Clear();
            channelNameToId.Clear();

            connectedEnetPeers.Clear();

            // MLAPI Channels
            for (byte i = 0; i < MLAPI_CHANNELS.Length; i++)
            {
                channelIdToName.Add(i, MLAPI_CHANNELS[i].Name);
                channelNameToId.Add(MLAPI_CHANNELS[i].Name, i);
                internalChannels.Add(i, new EnetChannel()
                {
                    Id = i,
                    Name = MLAPI_CHANNELS[i].Name,
                    Flags = MLAPIChannelTypeToPacketFlag(MLAPI_CHANNELS[i].Type)
                });
            }

            // Internal Channels
            for (int i = 0; i < Channels.Count; i++)
            {
                byte id = (byte)(i + MLAPI_CHANNELS.Length);

                channelIdToName.Add(id, Channels[i].Name);
                channelNameToId.Add(Channels[i].Name, id);
                internalChannels.Add(id, new EnetChannel()
                {
                    Id = id,
                    Name = Channels[i].Name,
                    Flags = Channels[i].Flags
                });
            }

            messageBuffer = new byte[MessageBufferSize];
        }

        private PacketFlags PacketFlagFromDelivery(EnetDelivery delivery)
        {
            switch (delivery)
            {
                case EnetDelivery.UnreliableSequenced:
                    return PacketFlags.None;
                case EnetDelivery.ReliableSequenced:
                    return PacketFlags.Reliable;
                case EnetDelivery.Unreliable:
                    return PacketFlags.Unsequenced;
                default:
                    return PacketFlags.None;
            }
        }

        public EnetDelivery MLAPIChannelTypeToPacketFlag(ChannelType type)
        {
            switch (type)
            {
                case ChannelType.Unreliable:
                    {
                        return EnetDelivery.Unreliable;
                    }
                case ChannelType.Reliable:
                    {
                        // ENET Does not support ReliableUnsequenced.
                        // https://github.com/MidLevel/MLAPI.Transports/pull/5#issuecomment-498311723
                        return EnetDelivery.ReliableSequenced;
                    }
                case ChannelType.ReliableSequenced:
                    {
                        return EnetDelivery.ReliableSequenced;
                    }
                case ChannelType.ReliableFragmentedSequenced:
                    {
                        return EnetDelivery.ReliableSequenced;
                    }
                case ChannelType.UnreliableSequenced:
                    {
                        return EnetDelivery.UnreliableSequenced;
                    }
                default:
                    {
                        throw new ArgumentOutOfRangeException(nameof(type), type, null);
                    }
            }
        }

        public ulong GetMLAPIClientId(uint peerId, bool isServer)
        {
            if (isServer)
            {
                return 0;
            }
            else
            {
                return peerId + 1;
            }
        }

        public void GetEnetConnectionDetails(ulong clientId, out uint peerId)
        {
            if (clientId == 0)
            {
                peerId = serverPeerId;
            }
            else
            {
                peerId = (uint)clientId - 1;
            }
        }
    }
}
