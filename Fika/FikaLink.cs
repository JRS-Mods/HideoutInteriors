using System;
using System.Collections.Generic;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;

namespace HideoutInteriors.Fika
{
    public struct InteriorsHelloPacket : INetSerializable
    {
        public string Version;
        public bool Welcome;

        public readonly void Serialize(NetDataWriter writer) { writer.Put(Version ?? ""); writer.Put(Welcome); }
        public void Deserialize(NetDataReader reader) { Version = reader.GetString(); Welcome = reader.GetBool(); }
    }

    /// <summary>A player's layout: whose it is, whether they host the raid, and the layout as text.</summary>
    public struct InteriorsLayoutPacket : INetSerializable
    {
        public string From, Layout;
        // Keep: its owner shares layouts, so it may be kept in the Layouts tab (a host's is always sent, for raids
        // on a copy of the hideout, but only kept if the host shares).
        public bool FromHost, Keep;
        // The network's strings stop at 65,535 bytes, and a decorated hideout can say more than that.
        private const int Chunk = 16000;

        public readonly void Serialize(NetDataWriter writer)
        {
            writer.Put(From ?? "");
            writer.Put(FromHost);
            writer.Put(Keep);
            string text = Layout ?? "";
            int chunks = (text.Length + Chunk - 1) / Chunk;
            writer.Put(chunks);
            for (int c = 0; c < chunks; c++) writer.Put(text.Substring(c * Chunk, Math.Min(Chunk, text.Length - c * Chunk)));
        }

        public void Deserialize(NetDataReader reader)
        {
            From = reader.GetString();
            FromHost = reader.GetBool();
            Keep = reader.GetBool();
            int chunks = reader.GetInt();
            var text = new System.Text.StringBuilder();
            for (int c = 0; c < chunks; c++) text.Append(reader.GetString());
            Layout = text.ToString();
        }
    }

    /// <summary>
    /// In a Fika raid the players send one another their hideout layouts. A player says hello to the host; the host
    /// answers with its own layout and the ones it has from the others, and passes each new one on. Packets only go to
    /// players who've said hello: a game without this mod can't read them and would log an error for each.
    /// </summary>
    internal sealed class FikaLink : IShareLink
    {
        private readonly HashSet<NetPeer> peers = new HashSet<NetPeer>();
        // On the host: the layouts that came from the other players, by name, for the ones who join later.
        private readonly Dictionary<string, string> received = new Dictionary<string, string>();
        private IFikaNetworkManager network;
        private bool host, welcomed, started;
        private int helloTries;
        private float nextHello, createdAt;

        public FikaLink()
        {
            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(e => Safely(() => Created(e)));
            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerDestroyedEvent>(e => Safely(() => Destroyed(e)));
            FikaEventDispatcher.SubscribeEvent<PeerDisconnectedEvent>(e => Safely(() => { if (e.NetworkManager == network) peers.Remove(e.Peer); }));
            FikaEventDispatcher.SubscribeEvent<GameWorldStartedEvent>(_ => Safely(() => { started = true; helloTries = 0; nextHello = 0; }));
        }

        private static void Safely(Action act)
        {
            try { act(); }
            catch (Exception e) { Plugin.Log.LogWarning("Layout sharing: " + e); }
        }

        // A player whose host hasn't answered yet, and still may.
        public bool WaitingForHost => network != null && !host && !welcomed && (helloTries < 5 || Time.unscaledTime < nextHello);

        // Each frame: a player who hasn't been welcomed yet says hello again, a few times.
        public void Tick()
        {
            if (network == null || host || welcomed) return;
            // Once the raid's world is up (or, that not heard of, half a minute after the link was made).
            if (!started && Time.unscaledTime < createdAt + 30f) return;
            if (helloTries >= 5 || Time.unscaledTime < nextHello) return;
            helloTries++;
            nextHello = Time.unscaledTime + 4f;
            var hello = new InteriorsHelloPacket { Version = Plugin.Version };
            network.SendData(ref hello, DeliveryMethod.ReliableOrdered);
        }

        private void Created(FikaNetworkManagerCreatedEvent e)
        {
            Reset();
            network = e.Manager;
            createdAt = Time.unscaledTime;
            host = network is FikaServer;
            network.RegisterPacket<InteriorsHelloPacket, NetPeer>((p, from) => Safely(() => Hello(p, from)));
            network.RegisterPacket<InteriorsLayoutPacket, NetPeer>((p, from) => Safely(() => Layout(p, from)));
        }

        private void Destroyed(FikaNetworkManagerDestroyedEvent e)
        {
            if (e.Manager != network) return;
            network.UnregisterPacket<InteriorsHelloPacket>();
            network.UnregisterPacket<InteriorsLayoutPacket>();
            Reset();
            Interiors.LinkClosed();
        }

        private void Reset()
        {
            network = null; host = welcomed = started = false; helloTries = 0; nextHello = 0;
            peers.Clear(); received.Clear();
        }

        private InteriorsLayoutPacket Mine() =>
            new InteriorsLayoutPacket { From = Interiors.PlayerName(), Layout = Interiors.RaidSnapshot(), FromHost = host, Keep = Interiors.ShareLayouts.Value };

        private void Hello(InteriorsHelloPacket packet, NetPeer from)
        {
            if (host)
            {
                peers.Add(from);
                var welcome = new InteriorsHelloPacket { Version = Plugin.Version, Welcome = true };
                network.SendDataToPeer(ref welcome, DeliveryMethod.ReliableOrdered, from);
                // The host's own goes to everyone: it's what a raid on a copy of the hideout shows.
                var mine = Mine();
                network.SendDataToPeer(ref mine, DeliveryMethod.ReliableOrdered, from);
                foreach (var pair in received)
                {
                    var theirs = new InteriorsLayoutPacket { From = pair.Key, Layout = pair.Value, Keep = true };
                    network.SendDataToPeer(ref theirs, DeliveryMethod.ReliableOrdered, from);
                }
            }
            else if (packet.Welcome && !welcomed)
            {
                welcomed = true;
                Plugin.Log.LogInfo("The host has Hideout Interiors too: layouts are shared.");
                if (!Interiors.ShareLayouts.Value) return;
                var mine = Mine();
                network.SendData(ref mine, DeliveryMethod.ReliableOrdered);
            }
        }

        private void Layout(InteriorsLayoutPacket packet, NetPeer from)
        {
            if (host)
            {
                // From a player: kept here, and passed on to the others.
                packet.FromHost = false;
                string name = string.IsNullOrEmpty(packet.From) ? "a teammate" : packet.From;
                if (packet.Keep) received[name] = packet.Layout;
                foreach (NetPeer peer in peers)
                    if (peer != from) network.SendDataToPeer(ref packet, DeliveryMethod.ReliableOrdered, peer);
            }
            Interiors.ReceivedLayout(packet.From, packet.Layout, packet.FromHost, packet.Keep);
        }
    }
}
