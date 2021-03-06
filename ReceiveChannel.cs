﻿using System;
using System.Collections.Generic;

namespace FalconUDP
{
    internal class ReceiveChannel
    {
        private const float ADDITIONAL_PACKET_SEQ_INCREMENT_AMOUNT = 0.000001f;

        /// <summary>
        /// Number of unread packets ready for reading
        /// </summary>
        internal int Count { get; private set; }

        private SortedList<float,Packet> receivedPackets;
        private List<Packet> packetsRead;
        private float lastReceivedPacketSeq;
        private int maxReadDatagramSeq;
        private SendOptions channelType;
        private FalconPeer localPeer;
        private RemotePeer remotePeer;
        private bool isReliable;
        private bool isInOrder;
        private object channelLock;

        internal ReceiveChannel(SendOptions channelType, FalconPeer localPeer, RemotePeer remotePeer)
        {
            this.channelType    = channelType;
            this.localPeer      = localPeer;
            this.remotePeer     = remotePeer;
            this.isReliable     = (channelType & SendOptions.Reliable) == SendOptions.Reliable;
            this.isInOrder      = (channelType & SendOptions.InOrder) == SendOptions.InOrder;
            this.receivedPackets = new SortedList<float,Packet>();
            this.packetsRead    = new List<Packet>();
            this.channelLock    = new object();
        }

        // returns true if datagram is valid, otherwise it should be dropped any additional packets in it should not be processed
        internal bool TryAddReceivedPacket(ushort datagramSeq, 
            PacketType type, 
            byte[] buffer, 
            int index, 
            int count, 
            bool isFirstPacketInDatagram,
            out bool applicationPacketAdded)
        {
            lock (channelLock)
            {
                applicationPacketAdded = false; // until proven otherwise

                // validate seq in range
                if (isFirstPacketInDatagram)
                {
                    ushort min = unchecked((ushort)(lastReceivedPacketSeq - Settings.OutOfOrderTolerance));
                    ushort max = unchecked((ushort)(lastReceivedPacketSeq + Settings.OutOfOrderTolerance));

                    // NOTE: Max could be less than min if exceeded MaxValue, likewise min could be 
                    //       greater than max if less than 0. So have to check seq between min - max range 
                    //       which is a loop, inclusive.

                    if (datagramSeq > max && datagramSeq < min)
                    {
                        localPeer.Log(LogLevel.Warning, String.Format("Out-of-order packet from: {0} dropped, out-of-order from last by: {1}.", remotePeer.PeerName, datagramSeq - lastReceivedPacketSeq));
                        return false;
                    }
                }

                // calc ordinal packet seq
                float ordinalPacketSeq;
                if (isFirstPacketInDatagram)
                {
                    ordinalPacketSeq = datagramSeq;
                    int diff = Math.Abs(datagramSeq - (int)lastReceivedPacketSeq);
                    if (diff > Settings.OutOfOrderTolerance)
                    {
                        if (datagramSeq < lastReceivedPacketSeq) // i.e. seq must have looped since we have already validated seq in range
                        {
                            ordinalPacketSeq += ushort.MaxValue;
                        }
                    }
                }
                else
                {
                    // lastReceived Seq will be ordinal seq for previous packet in datagram
                    ordinalPacketSeq = lastReceivedPacketSeq + ADDITIONAL_PACKET_SEQ_INCREMENT_AMOUNT;
                }

                // check not duplicate, this ASSUMES we haven't received 65534 datagrams between reads!
                if (receivedPackets.ContainsKey(ordinalPacketSeq))
                {
                    localPeer.Log(LogLevel.Warning, String.Format("Duplicate packet from: {0} dropped.", remotePeer.PeerName));
                    return false;
                }

                // if datagram required to be in order check after max read, if not drop it
                if (isFirstPacketInDatagram && isInOrder)
                {
                    if (ordinalPacketSeq < maxReadDatagramSeq)
                    {
                        if (isReliable)
                            remotePeer.ACK(datagramSeq, PacketType.AntiACK, channelType);
                        return false;
                    }
                }

                lastReceivedPacketSeq = ordinalPacketSeq;

                // if datagram requries ACK - send it!
                if (isFirstPacketInDatagram && isReliable)
                {
                    remotePeer.ACK(datagramSeq, PacketType.ACK, channelType);
                }

                switch (type)
                {
                    case PacketType.Application:
                        {
                            Packet packet = localPeer.PacketPool.Borrow();
                            packet.ElapsedMillisecondsSinceSent = remotePeer.Latency;
                            packet.ElapsedTimeAtReceived = localPeer.Stopwatch.ElapsedMilliseconds;
                            packet.PeerId = remotePeer.Id;
                            packet.WriteBytes(buffer, index, count);
                            packet.IsReadOnly = true;
                            packet.ResetPos();
                            packet.DatagramSeq = datagramSeq;

                            // Add packet
                            receivedPackets.Add(ordinalPacketSeq, packet);
                            
                            if (isReliable)
                            {
                                // re-calc number of continuous seq from first
                                Count = 1;
                                int key = receivedPackets[receivedPackets.Keys[0]].DatagramSeq;
                                for (int i = 1; i < receivedPackets.Count; i++)
                                {
                                    int next = receivedPackets[receivedPackets.Keys[i]].DatagramSeq;
                                    if (next == key)
                                    {
                                        // NOTE: This must be an additional packet with the same 
                                        //       datagram seq.

                                        Count++;
                                    }
                                    else if(next == (key + 1))
                                    {
                                        Count++;
                                        key = next;
                                    }
                                    else
                                    {
                                        break;
                                    }
                                }
                            }
                            else
                            {
                                Count++;
                            }
                            
                            applicationPacketAdded = true;
                            return true;
                        }
                    case PacketType.KeepAlive:
                        {
                            if (!remotePeer.IsKeepAliveMaster)
                            {
                                // To have received a KeepAlive from this peer who is not the KeepAlive
                                // master is only valid when the peer never received a KeepAlive from 
                                // us for Settings.KeepAliveAssumeMasterInterval for which the most 
                                // common cause would be we disappered though we must be back up again 
                                // to have received it! 

                                localPeer.Log(LogLevel.Warning, String.Format("Received KeepAlive from: {0} who's not the KeepAlive master!", remotePeer.EndPoint));
                            }

                            // nothing else to do we would have already ACK'd this message

                            return true;
                        }
                    case PacketType.AcceptJoin:
                        {
                            // nothing else to do we would have already ACK'd this msg
                            return true;
                        }
                    default:
                        {
                            localPeer.Log(LogLevel.Warning, String.Format("Dropped datagram, type: {0} from {1} - unexpected type", type, remotePeer.PeerName));
                            return false;
                        }
                }
            }
        }

        internal List<Packet> Read()
        {
            lock (channelLock)
            {
                packetsRead.Clear();

                if (Count > 0)
                {
                    if (isReliable)
                    {
                        while (Count > 0)
                        {
                            maxReadDatagramSeq = receivedPackets[receivedPackets.Keys[receivedPackets.Count - 1]].DatagramSeq;
                            packetsRead.Add(receivedPackets[receivedPackets.Keys[0]]);
                            receivedPackets.RemoveAt(0);
                            Count--;
                        }
                    }
                    else
                    {
                        packetsRead.Capacity = receivedPackets.Count;
                        packetsRead.AddRange(receivedPackets.Values);
                        maxReadDatagramSeq =  receivedPackets[receivedPackets.Keys[receivedPackets.Count-1]].DatagramSeq;
                        receivedPackets.Clear();
                        Count = 0;
                    }

                    // If max read seq > (ushort.MaxValue + Settings.OutOfOrderTolerance) no future 
                    // datagram will be from the old loop (without being dropped), so reset max and 
                    // ordinal seq to the same value as seq they are for.

                    if (maxReadDatagramSeq > Settings.MaxNeccessaryOrdinalSeq)
                    {
                        maxReadDatagramSeq -= ushort.MaxValue;
                        lastReceivedPacketSeq -= ushort.MaxValue;
                    }
                
                    // add time spent since recieved to latency estimate to ElapsedMillisecondsSinceSent
                    packetsRead.ForEach(p =>
                        {
                            p.ElapsedMillisecondsSinceSent += (int)(localPeer.Stopwatch.ElapsedMilliseconds - p.ElapsedTimeAtReceived);
                        });

                }

                return packetsRead;
            }
        }
    }
}
