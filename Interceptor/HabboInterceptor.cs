﻿using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Collections.Generic;

using Interceptor.Habbo;
using Interceptor.Memory;
using Interceptor.Logging;
using Interceptor.Encryption;
using Interceptor.Interception;
using Interceptor.Communication;

using Flazzy.ABC;
using Flazzy.Tags;

namespace Interceptor
{
    public class HabboInterceptor : Interception.Interceptor
    {
        public delegate Task PacketEvent(Packet packet);
        public delegate Task LogEvent(LogMessage message);
        public PacketEvent Incoming { get; set; }
        public PacketEvent Outgoing { get; set; }
        public LogEvent Log { get; set; }
        private PacketInformation[] InMessages { get; } = new PacketInformation[4001];
        private PacketInformation[] OutMessages { get; } = new PacketInformation[4001];
        public string Production { get; private set; }
        public bool PauseIncoming { get; set; }
        public bool PauseOutgoing { get; set; }

        private RC4Key DecipherKey { get; set; }
        private RC4Key CipherKey { get; set; }

        public HabboInterceptor() : base(IPAddress.Parse("127.0.0.1"), HostHelper.GetIPAddressFromHost("game-us.habbo.com"), 38101)
        {
        }

        public override void Start()
        {
            if (!HostHelper.TryAddRedirect(ClientIp.ToString(), "game-us.habbo.com"))
            {
                LogInternalAsync(new LogMessage(LogSeverity.Error, "Failed to add host redirect.")).Wait();
                throw new Exception("Failed to add host redirect.");
            }
            else
            {
                Interception.Interceptor interceptor = new Interception.Interceptor(ClientIp, ClientPort, ServerIp, ServerPort);
                interceptor.Start();
                interceptor.Connected += () =>
                {
                    Connected += () => LogInternalAsync(!HostHelper.TryRemoveRedirects()
                        ? new LogMessage(LogSeverity.Warning, "Failed to remove host redirect.")
                        : new LogMessage(LogSeverity.Info, "Connected."));

                    base.Start();
                    return Task.CompletedTask;
                };
            }
        }

        public override void Stop()
        {
            HostHelper.TryRemoveRedirects();
            base.Stop();
        }

        internal async Task LogInternalAsync(LogMessage message)
        {
            if (Log != null)
            {
                Delegate[] delegates = Log.GetInvocationList();
                for (int i = 0; i < delegates.Length; i++)
                {
                    try
                    {
                        await ((LogEvent)delegates[i])(message).ConfigureAwait(false);
                    }
                    catch { }
                }
            }
        }

        public Task SendToServerAsync(Packet packet)
        {
            return SendInternalAsync(Server, packet);
        }

        public Task SendToClientAsync(Packet packet)
        {
            return SendInternalAsync(Client, packet);
        }

        internal async Task SendInternalAsync(TcpClient client, Packet packet)
        {
            if (!packet.Blocked && packet.Valid)
            {
                bool outgoing = client == Server;
                PacketEvent packetEvent = (outgoing ? Outgoing : Incoming);
                if (packetEvent != null)
                {
                    Delegate[] delegates = packetEvent.GetInvocationList();
                    for (int i = 0; i < delegates.Length; i++)
                    {
                        try
                        {
                            await ((PacketEvent)delegates[i])(packet).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            await LogInternalAsync(new LogMessage(LogSeverity.Warning, "An exception was thrown in a packet event handler", e));
                        }
                    }
                }

                if (!packet.Blocked && packet.Valid)
                {
                    Memory<byte> packetBytes = packet.Construct();
                    if (outgoing) CipherKey?.Cipher(packetBytes);
                    await client.GetStream().WriteAsync(packetBytes).ConfigureAwait(false);
                }
            }
        }

        private async Task DisassembleAsync(string clientUrl)
        {
            string swfUrl = string.Concat(clientUrl, "Habbo.swf");
            using (WebClient wc = new WebClient())
            await using (Stream stream = await wc.OpenReadTaskAsync(swfUrl))
            using (HGame game = new HGame(stream))
            {
                await LogInternalAsync(new LogMessage(LogSeverity.Info, "Disassembling SWF."));
                game.Disassemble();
                game.GenerateMessageHashes();

                foreach ((ushort id, HMessage message) in game.InMessages)
                {
                    InMessages[id] = new PacketInformation(message.Id, message.Hash, message.Structure);
                    message.Class = null;
                    message.Parser = null;
                    message.References.Clear();
                }

                foreach ((ushort id, HMessage message) in game.OutMessages)
                {
                    OutMessages[id] = new PacketInformation(message.Id, message.Hash, message.Structure);
                    message.Class = null;
                    message.Parser = null;
                    message.References.Clear();
                }

                foreach (ABCFile abc in game.ABCFiles)
                {
                    ((Dictionary<ASMultiname, List<ASClass>>)abc.GetType().GetField("_classesCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(abc)).Clear();

                    abc.Methods.Clear();
                    abc.Metadata.Clear();
                    abc.Instances.Clear();
                    abc.Classes.Clear();
                    abc.Scripts.Clear();
                    abc.MethodBodies.Clear();

                    abc.Pool.Integers.Clear();
                    abc.Pool.UIntegers.Clear();
                    abc.Pool.Doubles.Clear();
                    abc.Pool.Strings.Clear();
                    abc.Pool.Namespaces.Clear();
                    abc.Pool.NamespaceSets.Clear();
                    abc.Pool.Multinames.Clear();

                    abc.Dispose();
                }

                game.Tags.Clear();
                ((Dictionary<ASClass, HMessage>)typeof(HGame).GetField("_messages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(game)).Clear();
                ((Dictionary<DoABCTag, ABCFile>)typeof(HGame).GetField("_abcFileTags", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(game)).Clear();
                game.ABCFiles.Clear();
            }

            GC.Collect();
        }

        private async Task InterceptKeyAsync()
        {
            await Task.Delay(1000); // Wait for client to finish sending the first 3 packets

            Memory<byte> buffer = new byte[1024];
            int decipherBytesRead = await Client.GetStream().ReadAsync(buffer);

            if (RC4Extractor.TryExtractKey(out RC4Key key))
            {
                await LogInternalAsync(new LogMessage(LogSeverity.Info, string.Format("RC4: {0}", key)));
                Memory<byte> decipherBuffer = new byte[decipherBytesRead];

                for (int i = 0; i < 256; i++)
                {
                    for (int j = 0; j < 256; j++)
                    {
                        RC4Key tempKey = key.Copy(i, j);
                        tempKey.Reverse(decipherBytesRead);
                        if (tempKey.X == 0 && tempKey.Y == 0)
                        {
                            buffer.Slice(0, decipherBytesRead).CopyTo(decipherBuffer);
                            RC4Key possibleDecipherKey = tempKey.Copy();
                            possibleDecipherKey.Cipher(decipherBuffer);

                            IReadOnlyCollection<Packet> packets = Packet.Parse(decipherBuffer);
                            if (packets.Count == 0)
                                continue;

                            CipherKey = tempKey;
                            DecipherKey = possibleDecipherKey;

                            bool disassembledClient = false;
                            foreach (Packet packet in packets)
                            {
                                if (!disassembledClient)
                                {
                                    await DisassembleAsync(packet.ReadString(4));
                                    disassembledClient = true;
                                }
                                await SendInternalAsync(Server, packet);
                            }

                            return;
                        }
                    }
                }
            }
            else await LogInternalAsync(new LogMessage(LogSeverity.Error, "Could not find RC4 key."));
        }

        private async Task<bool> ReceiveAsync(NetworkStream stream, Memory<byte> buffer)
        {
            int bytesRead = 0;
            do
            {
                int tempBytesRead = await stream.ReadAsync(buffer.Slice(bytesRead));
                if (tempBytesRead == 0)
                    return false;

                bytesRead += tempBytesRead;
            }
            while (bytesRead != buffer.Length);
            return true;
        }

        protected override async Task InterceptAsync(TcpClient client, TcpClient server)
        {
            int outgoingCount = 0;
            bool outgoing = server == Server;
            Memory<byte> buffer = new byte[3072];
            Memory<byte> lengthBuffer = new byte[4];

            NetworkStream clientStream = client.GetStream();
            try
            {
                while (IsConnected)
                {
                    if ((outgoing && PauseOutgoing) || (!outgoing && PauseIncoming))
                    {
                        await Task.Delay(20);
                        continue;
                    }

                    if (outgoing && CipherKey == null)
                    {
                        if (outgoingCount != 5)
                            outgoingCount++;

                        if (outgoingCount == 4)
                            await InterceptKeyAsync();
                    }

                    if (!await ReceiveAsync(clientStream, lengthBuffer))
                        break;

                    if (outgoing)
                        DecipherKey?.Cipher(lengthBuffer);

                    if (BitConverter.IsLittleEndian)
                        lengthBuffer.Span.Reverse();
                    int length = BitConverter.ToInt32(lengthBuffer.Span);

                    Memory<byte> packetBytes = length > buffer.Length ? new byte[length] : buffer.Slice(0, length);
                    if (!await ReceiveAsync(clientStream, packetBytes))
                        break;

                    if (outgoing)
                        DecipherKey?.Cipher(packetBytes);

                    Packet packet = new Packet(length, packetBytes);
                    PacketInformation[] messages = outgoing ? OutMessages : InMessages;
                    PacketInformation packetInfo = messages[packet.Header];
                    if (packetInfo.Id != 0)
                    {
                        packet.Hash = packetInfo.Hash;
                        packet.Structure = packetInfo.Structure;
                    }

                    if (outgoing)
                    {
                        if (outgoingCount == 1)
                            Production = packet.ReadString(0);

                        await SendInternalAsync(Server, packet);
                    }
                    else
                        await SendInternalAsync(Client, packet);
                }
            }
            catch (IOException) { }
            catch (Exception e)
            {
                await LogInternalAsync(new LogMessage(LogSeverity.Error, "An exception was thrown during packet interception", e));
            }
            finally
            {
                if (IsConnected)
                {
                    await LogInternalAsync(new LogMessage(LogSeverity.Info, "Disconnected."));
                    Stop();
                }
            }
        }
    }
}