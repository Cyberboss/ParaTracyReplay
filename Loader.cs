﻿using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;

using K4os.Compression.LZ4.Streams;

using Overby.Extensions.AsyncBinaryReaderWriter;

using Serilog;

using ParaTracyReplay.Structures;
using ParaTracyReplay.Structures.File;
using ParaTracyReplay.Structures.Network;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams.Frames;
using System.Diagnostics;
using static System.Net.Mime.MediaTypeNames;
using System.Buffers.Binary;
using System;

namespace ParaTracyReplay
{
    /// <summary>
    /// The actual loader implementation.
    /// This loads the file up and sends it over the network to a Tracy capture client.
    /// </summary>
    static class Loader
    {
        /// <summary>
        /// Loads the Tracy file from disk and sends it over the network.
        /// </summary>
        /// <param name="file_to_load">The <see cref="string"/> path to the file we want to load.</param>
        /// <returns>A <see cref="ValueTask"/> representing the running operation.</returns>
        public static async ValueTask<int> Load(string file_to_load)
        {
            // Make sure it exists first
            if (!File.Exists(file_to_load))
            {
                Log.Logger.Fatal($"File \"{file_to_load}\" not found!");
                return 1;
            }

            Log.Logger.Information($"Loading \"{file_to_load}\"...");

            // Read it as a file stream not just as the full thing at once
            await using FileStream fs = new(file_to_load, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using AsyncBinaryReader br = new(fs, Encoding.UTF8, true);
            // Get the file header first
            FileHeader file_header = new FileHeader();
            await file_header.Read(br);

            // So we can validate its signature
            if (file_header.Signature == Constants.FileSignature)
            {
                Log.Logger.Information("File signature matches");
            }
            else
            {
                Log.Logger.Fatal($"File signature mismatch! Expected \"{Constants.FileSignature}\", got \"{file_header.Signature}\"");
                return 1;
            }

            // And version
            if (file_header.Version == Constants.FileVersion)
            {
                Log.Logger.Information("File version matches");
            }
            else
            {
                Log.Logger.Fatal($"File version mismatch! Expected \"{Constants.FileVersion}\", got \"{file_header.Version}\"");
                return 1;
            }

            // Inform of the process name
            Log.Logger.Information($"Captured process: {string.Join("", file_header.ProgramName)}");

            // Get how many source locations we have
            uint source_locations_count = await br.ReadUInt32Async();
            Log.Logger.Information($"Found {source_locations_count} source locations");

            // Save our cache of them
            Dictionary<long, string> strings = new Dictionary<long, string>();
            Dictionary<ulong, SourceLocation> source_locations = new Dictionary<ulong, SourceLocation>();

            // Read them all in
            for (ulong i = 0; i < source_locations_count; i++)
            {
                // Track the location name
                uint loc_name_length = await br.ReadUInt32Async();
                string loc_name = Encoding.ASCII.GetString(await br.ReadBytesAsync((int)loc_name_length));

                // And the function name
                uint function_name_len = await br.ReadUInt32Async();
                string function_name = Encoding.ASCII.GetString(await br.ReadBytesAsync((int)function_name_len));

                // And the file name
                uint file_name_len = await br.ReadUInt32Async();
                string file_name = Encoding.ASCII.GetString(await br.ReadBytesAsync((int)file_name_len));

                // And the file name
                uint line = await br.ReadUInt32Async();
                uint colour = await br.ReadUInt32Async();

                // Get the int of the name
                long name_int = 0;
                if (!string.IsNullOrWhiteSpace(loc_name))
                {
                    name_int = loc_name.GetHashCode();
                    while (strings.TryGetValue(name_int, out string? hash_match) && hash_match != loc_name)
                        ++name_int;

                    strings[name_int] = loc_name;
                }

                // And of the function
                long function_int = 0;
                if (!string.IsNullOrWhiteSpace(function_name))
                {
                    function_int = function_name.GetHashCode();
                    while (strings.TryGetValue(name_int, out string? hash_match) && hash_match != function_name)
                        ++function_int;

                    strings[function_int] = function_name;
                }

                // And of the file
                long file_int = 0;
                if (!string.IsNullOrWhiteSpace(file_name))
                {
                    file_int = file_name.GetHashCode();
                    while (strings.TryGetValue(name_int, out string? hash_match) && hash_match != file_name)
                        ++file_int;

                    strings[file_int] = file_name;
                }

                // And pack it all into a nice object
                source_locations[i] = new SourceLocation()
                {
                    Name = name_int,
                    Function = function_int,
                    File = file_int,
                    Line = line,
                    Colour = colour,
                };
            }

            // Inform of load
            Log.Logger.Information($"Successfully loaded {source_locations.Count} source locations");


            async ValueTask<Socket> Accept()
            {
                // Create our server endpoint
                IPEndPoint socket_endpoint = new IPEndPoint(IPAddress.Any, 8086);
                using Socket server = new Socket(SocketType.Stream, ProtocolType.Tcp);
                server.Bind(socket_endpoint);
                // Allow 1 client max
                server.Listen(1);

                // Wait here
                Log.Logger.Information($"Waiting for connection on {socket_endpoint.Address}:{socket_endpoint.Port}...");

                return await server.AcceptAsync();
            }

            using Socket socket = await Accept();

            DateTimeOffset startTime = DateTimeOffset.Now;
            Log.Logger.Information($"Connection established");

            // Read 8 bytes for the client name
            byte[] data_buffer = ArrayPool<byte>.Shared.Rent(8);
            string client_name;
            try
            {
                await socket.ReceiveAsync(new ArraySegment<byte>(data_buffer, 0, 8), SocketFlags.None);
                client_name = Encoding.ASCII.GetString(data_buffer, 0, 8);

                // Validate its a Tracy client
                if (client_name != "TracyPrf")
                {
                    Log.Logger.Fatal($"Invalid client (Expected \"TracyPrf\", got \"{client_name}\")");
                    return 1;
                }

                // Read 4 bytes for protocol number
                await socket.ReceiveAsync(new ArraySegment<byte>(data_buffer, 0, 4), SocketFlags.None);
                uint protocol_version = BitConverter.ToUInt32(data_buffer);

                // We only support these protocol versions
                uint[] valid_versions = new uint[] { 56, 57 };

                // Validate protocol version
                if (!valid_versions.Contains(protocol_version))
                {
                    Log.Logger.Fatal($"Invalid protocol version (Got {protocol_version}, valid versions are {string.Join(", ", valid_versions)})");
                    data_buffer[0] = Constants.NetworkHandshakeProtocolMismatch;
                    await socket.SendAsync(new ArraySegment<byte>(data_buffer), SocketFlags.None);
                    return 1;
                }

                // If we got here its a valid version, send our welcome
                data_buffer[0] = Constants.NetworkHandshakeWelcome;
                await socket.SendAsync(new ArraySegment<byte>(data_buffer, 0, 1), SocketFlags.None);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(data_buffer);
            }

            // Make our network header from the file header
            NetworkHeader net_header = new NetworkHeader();
            net_header.Multiplier = file_header.Multiplier;
            net_header.InitBegin = file_header.InitBegin;
            net_header.InitEnd = file_header.InitEnd;
            net_header.Delay = file_header.Delay;
            net_header.Resolution = file_header.Resolution;
            net_header.Epoch = file_header.Epoch;
            net_header.ExecTime = file_header.ExecTime;
            net_header.ProcessId = file_header.ProcessId;
            net_header.SamplingPeriod = file_header.SamplingPeriod;
            net_header.Flags = (sbyte)file_header.Flags;
            net_header.CpuArch = (sbyte)file_header.CpuArch;
            Array.Copy(file_header.CpuManufacturer, net_header.CpuManufacturer, 12);
            net_header.CpuId = file_header.CpuId;
            Array.Copy(file_header.ProgramName, net_header.ProgramName, 64);
            Array.Copy(file_header.HostInfo, net_header.HostInfo, 64);

            // Send the network header
            await using NetworkStream socketStream = new NetworkStream(socket, false);

            // Setup some vars for the network handling
            const int event_array_size = 24;
            int maximumFrameSize = (int)Math.Floor((decimal)(Constants.NetworkMaxFrameSize / event_array_size));

            long timestamp = 0;
            long threadId = 0;

            var encoderSettings = new LZ4EncoderSettings
            {
                CompressionLevel = LZ4Level.L00_FAST,
            };

            async ValueTask FirstWrite()
            {
                using var socketWriter = new AsyncBinaryWriter(socketStream, Encoding.ASCII, true);
                await net_header.Write(socketWriter);
            }

            ValueTask lastSocketWrite = FirstWrite();

            byte[]? frameBuffer = null;
            MemoryStream? frameStream = null;
            AsyncBinaryWriter? frameWriter = null;

            async ValueTask CloseFrame()
            {
                int encodedSize;
                byte[]? encodedBuffer = null;
                try
                {
                    const int SendHeaderSize = 4;
                    try
                    {
                        // Get the amount written
                        frameWriter?.Dispose();
                        frameWriter = null;

                        if (frameStream == null)
                            return;

                        var frameLength = (int)frameStream.Position;
                        await frameStream.DisposeAsync();
                        frameStream = null;

                        var encodedMaxSize = LZ4Codec.MaximumOutputSize(frameLength);
                        var encodedBufferSize = encodedMaxSize + SendHeaderSize;
                        encodedBuffer = ArrayPool<byte>.Shared.Rent(encodedBufferSize);
                        encodedSize = LZ4Codec.Encode(frameBuffer, 0, frameLength, encodedBuffer, SendHeaderSize, encodedMaxSize);
                    }
                    finally
                    {
                        if (frameBuffer != null)
                            ArrayPool<byte>.Shared.Return(frameBuffer);
                    }

                    BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(encodedBuffer), encodedSize);

                    var awaiting = lastSocketWrite;
                    async ValueTask QueueSocketWrite()
                    {
                        var ownedBuffer = encodedBuffer;
                        encodedBuffer = null;
                        try
                        {
                            await awaiting;
                            var sendSpan = new ReadOnlyMemory<byte>(ownedBuffer, 0, encodedSize + SendHeaderSize);
                            await socketStream.WriteAsync(sendSpan);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(ownedBuffer);
                        }
                    }

                    lastSocketWrite = QueueSocketWrite();
                }
                finally
                {
                    if (encodedBuffer != null)
                        ArrayPool<byte>.Shared.Return(encodedBuffer);
                }
            }


            async ValueTask NewFrame()
            {
                await CloseFrame();
                frameBuffer = ArrayPool<byte>.Shared.Rent(maximumFrameSize);
                frameStream = new MemoryStream(frameBuffer, 0, maximumFrameSize, true, false);
                frameWriter = new AsyncBinaryWriter(frameStream, Encoding.ASCII, true);
            }

            try
            {
                ValueTask lastFrameWrite = NewFrame();
                try
                {
                    void QueueEncodedWrite(StructureBase next)
                    {
                        var awaiting = lastFrameWrite;
                        async ValueTask Next()
                        {
                            await awaiting;
                            var initialPosition = frameStream!.Position;
                            var expectedNewPosition = initialPosition + next.WriteSize;
                            var needNewFrame = expectedNewPosition > maximumFrameSize;
                            if (needNewFrame)
                                await NewFrame();

                            await next.Write(frameWriter!);
                        }

                        lastFrameWrite = Next();
                    }

                    void QueueEncodedByteWrite(byte thing)
                    {
                        var awaiting = lastFrameWrite;
                        async ValueTask Next()
                        {
                            await awaiting;
                            if (frameStream!.Position + 1 > maximumFrameSize)
                                await NewFrame();

                            await frameWriter!.WriteAsync(thing);
                        }

                        lastFrameWrite = Next();
                    }

                    void QueueWriteThreadContext(uint passed_threadid)
                    {
                        // If its different, send a new thread and reset the timestamp
                        if (threadId == passed_threadid)
                            return;

                        threadId = passed_threadid;
                        timestamp = 0;

                        // Make the event
                        NetworkThreadContext netthread_event = new NetworkThreadContext()
                        {
                            ThreadId = passed_threadid,
                            Type = Constants.NetworkEventThreadContext
                        };

                        // And fire
                        QueueEncodedWrite(netthread_event);
                    }

                    void QueueWriteStringResponse(string str, ulong pointer, byte stringtype)
                    {
                        // Make the event
                        NetworkStringData string_packet = new NetworkStringData()
                        {
                            Type = stringtype,
                            Pointer = pointer,
                            String = str.ToCharArray()
                        };

                        // And fire
                        QueueEncodedWrite(string_packet);
                    }

                    async ValueTask ReceiveTask()
                    {
                        // Set this timeout to account for the fact we dont handle the data done packet if that even exists
                        socket.ReceiveTimeout = 1;
                        using var receiveReader = new AsyncBinaryReader(socketStream, Encoding.UTF8, true);
                        while (true)
                        {
                            // Decode the request
                            NetworkRequest nr = new NetworkRequest();
                            if (!await nr.Read(receiveReader))
                                break;


                            // Figure out what we have
                            switch (nr.Type)
                            {
                                // Source location query, handle it
                                case Constants.NetworkQuerySrcloc:
                                    SourceLocation sourceloc = source_locations[BitConverter.ToUInt64(BitConverter.GetBytes(nr.Pointer))];
                                    NetworkSourceLocation sourceloc_packet = new NetworkSourceLocation()
                                    {
                                        Type = Constants.NetworkEventSrcloc,
                                        Name = sourceloc.Name,
                                        Function = sourceloc.Function,
                                        File = sourceloc.File,
                                        Line = sourceloc.Line,
                                        ColourR = (byte)((sourceloc.Colour >> 0x00) & 0xFF),
                                        ColourG = (byte)((sourceloc.Colour >> 0x08) & 0xFF),
                                        ColourB = (byte)((sourceloc.Colour >> 0x10) & 0xFF)
                                    };
                                    QueueEncodedWrite(sourceloc_packet);
                                    break;

                                // String query, handle it
                                case Constants.NetworkQueryString:
                                    QueueWriteStringResponse(strings[nr.Pointer], BitConverter.ToUInt64(BitConverter.GetBytes(nr.Pointer)), Constants.NetworkResponseStringData);
                                    break;

                                // No symbol code handling
                                case Constants.NetworkQuerySymbolCode:
                                    QueueEncodedByteWrite(Constants.NetworkResponseSymbolCodeNotAvailable);
                                    break;

                                // No source code handling
                                case Constants.NetworkQuerySourceCode:
                                    QueueEncodedByteWrite(Constants.NetworkResponseSourceCodeNotAvailable);
                                    break;

                                // No data transfer handling
                                case Constants.NetworkQueryDataTransfer:
                                    QueueEncodedByteWrite(Constants.NetworkResponseServerQueryNoop);
                                    break;

                                // No partial data transfer handling
                                case Constants.NetworkQueryDataTransferPart:
                                    QueueEncodedByteWrite(Constants.NetworkResponseServerQueryNoop);
                                    break;

                                // Handle thread names. Only one.
                                case Constants.NetworkQueryThreadString:
                                    QueueWriteStringResponse("main", BitConverter.ToUInt64(BitConverter.GetBytes(nr.Pointer)), Constants.NetworkResponseThreadName);
                                    break;

                                // Infom on packets we dont recognise
                                default:
                                    Log.Logger.Warning($"Unknown request from Tracy (Packet type: {nr.Type})");
                                    break;
                            }
                        }
                    }

                    Log.Logger.Information("Sending proc events to client...");

                    // Now read file events and send them off
                    while (true)
                    {
                        // Make a new event and read the binary in from the file
                        FileEvent file_event = new FileEvent();
                        if (!await file_event.Read(br))
                            break;

                        // Figure out what event we have
                        switch (file_event.Type)
                        {
                            // Handle ZoneBegin event
                            case Constants.FileEventZoneBegin:
                                FileZoneBegin file_zonebegin = (FileZoneBegin)file_event.Event;
                                QueueWriteThreadContext(file_zonebegin.ThreadId);

                                NetworkZoneBegin network_startevent = new NetworkZoneBegin()
                                {
                                    Type = Constants.NetworkEventZoneBegin,
                                    Timestamp = file_zonebegin.Timestamp - timestamp,
                                    SourceLocation = file_zonebegin.SourceLocation
                                };

                                QueueEncodedWrite(network_startevent);
                                timestamp = file_zonebegin.Timestamp;
                                break;


                            // Handle ZoneEnd event
                            case Constants.FileEventZoneEnd:
                                FileZoneEnd file_zoneend = (FileZoneEnd)file_event.Event;
                                QueueWriteThreadContext(file_zoneend.ThreadId);

                                NetworkZoneEnd network_endevent = new NetworkZoneEnd()
                                {
                                    Type = Constants.NetworkEventZoneEnd,
                                    Timestamp = file_zoneend.Timestamp - timestamp
                                };

                                QueueEncodedWrite(network_endevent);
                                timestamp = file_zoneend.Timestamp;
                                break;


                            // Handle ZoneColour event
                            case Constants.FileEventZoneColour:
                                FileZoneColour file_zonecolour = (FileZoneColour)file_event.Event;
                                QueueWriteThreadContext(file_zonecolour.ThreadId);

                                NetworkZoneColour network_colourevent = new NetworkZoneColour()
                                {
                                    Type = Constants.NetworkEventZoneColour,
                                    ColourR = (byte)((file_zonecolour.Colour >> 0x00) & 0xFF),
                                    ColourG = (byte)((file_zonecolour.Colour >> 0x08) & 0xFF),
                                    ColourB = (byte)((file_zonecolour.Colour >> 0x10) & 0xFF)
                                };

                                QueueEncodedWrite(network_colourevent);
                                break;


                            // Handle FrameMark event
                            case Constants.FileEventFrameMark:
                                FileFrameMark file_framemarkevent = (FileFrameMark)file_event.Event;

                                NetworkFrameMark network_framemarkevent = new NetworkFrameMark()
                                {
                                    Type = Constants.NetworkEventFrameMark,
                                    Name = 0,
                                    Timestamp = file_framemarkevent.Timestamp
                                };

                                QueueEncodedWrite(network_framemarkevent);
                                break;
                        }
                    }

                    // We have read all the proc events, dump to the network and start the next
                    Log.Logger.Information("Flushing proc events...");
                    await lastFrameWrite;
                    lastFrameWrite = ValueTask.CompletedTask;
                    await lastSocketWrite;
                    lastSocketWrite = ValueTask.CompletedTask;

                    await NewFrame();

                    var totalTime = DateTimeOffset.Now - startTime;

                    Log.Logger.Information($"Proc events sent. Took {totalTime.TotalSeconds}s");

                    try
                    {
                        await ReceiveTask(); // TODO: Can be done in parallel with proc sending?
                    }
                    catch (SocketException exc)
                    {
                        // Timeouts are expected since we dont parse the "done" message
                        if (exc.SocketErrorCode != SocketError.TimedOut)
                        {
                            // If its not a timeout, kick up a fuss
                            throw exc;
                        }
                    }
                }
                finally
                {
                    await lastFrameWrite;
                }
            }
            finally
            {
                // Dump it all
                await CloseFrame();
            }

            await lastSocketWrite;

            Log.Logger.Information("Data transfer complete");

            await socket.DisconnectAsync(false);

            // And clean up
            Log.Logger.Information("Done!");
            return 0;
        }
    }
}
