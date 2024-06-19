// This file is provided under The MIT License as part of SqualiveNetworking.
// Copyright (c) Squalive-Studios
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/Squalive/SqualiveNetworking

using System;
using System.Runtime.InteropServices;
using AOT;
using SqualiveNetworking.SteamTransport.Utils;
using SqualiveNetworking.Utils;
using Steamworks;
using Steamworks.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Error;
using UnityEngine;

namespace SqualiveNetworking.SteamTransport
{
    public struct SteamNetworkInterface : INetworkInterface
    {
        private const Steamworks.Data.SendType SendFlags = Steamworks.Data.SendType.Unreliable | Steamworks.Data.SendType.NoDelay | Steamworks.Data.SendType.NoNagle;
        
        public NetworkEndpoint LocalEndpoint { get; private set; }

        public bool IsSteamServer;

        #region Server

        private NativeReference<Socket>  _socket;

        private NativeReference<HSteamNetPollGroup> _pollGroup;

        private NativeHashSet<ulong> _connecting;

        private NativeParallelHashMap<ulong, Steamworks.Data.Connection> _connected;

        #endregion

        #region Client

        private NativeReference<Steamworks.Data.Connection> _connection;

        #endregion

        private NativeReference<bool> _isValid;

        private bool IsValid => _isValid.IsCreated && _isValid.Value;

        private SteamNetworkParameters _parameters;
        
        public int Initialize( ref NetworkSettings settings, ref int packetPadding )
        {
            if ( !settings.TryGet( out _parameters ) )
            {
                _parameters.IsServer = 1;
                _parameters.UsingRelay = 0;
                _parameters.VirtualPort = 0;
            }

            _isValid = new NativeReference<bool>( false, Allocator.Persistent );
            
            LocalEndpoint = SteamClient.SteamId.ToNetworkEndpoint();

            if ( _parameters.IsServer.ToBoolean() )
            {
                var pollGroup = IsSteamServer ? SteamNetworkingSockets.CreatePollGroup_Server() : SteamNetworkingSockets.CreatePollGroup_Client();

                _pollGroup = new NativeReference<HSteamNetPollGroup>( pollGroup, Allocator.Persistent );

                _connecting = new NativeHashSet<ulong>( Constants.HashSetInitialCapacity, Allocator.Persistent );
                _connected = new NativeParallelHashMap<ulong, Steamworks.Data.Connection>( Constants.HashSetInitialCapacity, Allocator.Persistent );

                _socket = new NativeReference<Socket>( Allocator.Persistent );
            }
            else
            {
                _connection = new NativeReference<Steamworks.Data.Connection>( Allocator.Persistent );
            }
            
            SteamNetworkingSockets.OnConnectionStatusChanged += OnConnectionStatusChanged;

            return 0;
        }
        
        private unsafe struct ServerSendJob : IJob
        {
            public PacketsQueue SendQueue;

            public NativeParallelHashMap<ulong, Steamworks.Data.Connection>.ReadOnly Connections;

            public PortableFunctionPointer<NetMsgHelper.FreeDelegate> FreeFn;

            public bool IsSteamServer;
            
            public void Execute()
            {
                var count = SendQueue.Count;

                if ( count <= 0 )
                    return;
                
                var msgs = new UnsafePtrList<NetMsg>( count, Allocator.Temp, NativeArrayOptions.ClearMemory );
                var results = new NativeList<long>( count, Allocator.Temp );

                ProcessPacketProcessors( count, ref msgs );
                SendPackets( ref msgs, ref results );
                
                msgs.Dispose();
                results.Dispose();
            }

            private void ProcessPacketProcessors( int count, ref UnsafePtrList<NetMsg> msgs )
            {
                for ( var i = 0; i < count; i++ )
                {
                    var packetProcessor = SendQueue[i];

                    if ( packetProcessor.Length <= 0 )
                        continue;

                    void* data = (byte*)packetProcessor.GetUnsafePayloadPtr() + packetProcessor.Offset;

                    if ( !Connections.TryGetValue( packetProcessor.EndpointRef.ToSteamId(), out var connection ) )
                    {
#if ENABLE_SQUALIVE_NET_DEBUG
                        Debug.LogError($"Unable to send to steamID: {packetProcessor.EndpointRef.ToSteamId().Value}. Not found in {nameof(Connections)}.");        
#endif
                    }

                    var destBuffer = UnsafeUtility.Malloc( packetProcessor.Length, UnsafeUtility.AlignOf<byte>(), Allocator.Persistent );

                    UnsafeUtility.MemCpy( destBuffer, data, packetProcessor.Length );

                    var netMsg = SteamNetworkingUtils.AllocateMessage();
                    
                    netMsg->Connection = connection;
                    netMsg->Flags = SendFlags;
                    netMsg->DataPtr = (IntPtr)destBuffer;
                    netMsg->DataSize = packetProcessor.Length;
                    netMsg->FreeDataPtr = FreeFn.Ptr.Value;

                    msgs.Add( netMsg );
                }
                
                SendQueue.Clear();
            }

            private void SendPackets( ref UnsafePtrList<NetMsg> msgs, ref NativeList<long> results )
            {
                var msgPtr = msgs.Ptr;
                var resultsPTr = results.GetUnsafePtr();
                
                if ( IsSteamServer )
                {
                    SteamNetworkingSockets.SendMessages_Server( msgs.Length, msgPtr, resultsPTr );
                }
                else
                {
                    SteamNetworkingSockets.SendMessages_Client( msgs.Length, msgPtr, resultsPTr );
                }
            }
        }
        
        private unsafe struct ServerReceiveJob : IJob
        {
            public PacketsQueue ReceiveQueue;

            public OperationResult ReceiveResult;

            public NativeReference<HSteamNetPollGroup> PollGroup;

            public bool IsSteamServer;

            public int MaxMessageCount;
            
            public void Execute()
            {
                if ( !ReceiveQueue.EnqueuePacket( out var packetProcessor ) )
                {
                    ReceiveResult.ErrorCode = (int)StatusCode.NetworkReceiveQueueFull;
                    return;
                }
                
                int processed = 0;

                var buffer = new UnsafePtrList<NetMsg>( MaxMessageCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory );

                if ( IsSteamServer )
                {
                    processed = SteamNetworkingSockets.ReceiveMessagesOnPollGroup_Server( PollGroup.Value, (IntPtr)buffer.Ptr, MaxMessageCount );
                }
                else
                {
                    processed = SteamNetworkingSockets.ReceiveMessagesOnPollGroup_Client( PollGroup.Value, (IntPtr)buffer.Ptr, MaxMessageCount );
                }

                if ( processed == 0 )
                {
                    ReceiveResult.ErrorCode = (int)StatusCode.Success;
                    
                    return;
                }
                
                for ( int i = 0; i < processed; i++ )
                {
                    var netMsg = buffer.Ptr[ i ];

                    packetProcessor.EndpointRef = netMsg->Identity.SteamId.ToNetworkEndpoint();
                    packetProcessor.AppendToPayload( (void*)netMsg->DataPtr, netMsg->DataSize );

                    NetMsg.InternalRelease( netMsg );
                }
                
                ReceiveResult.ErrorCode = (int)StatusCode.Success;
            }
        }
        
        private unsafe struct ClientSendJob : IJob
        {
            public PacketsQueue SendQueue;
            
            public NativeReference<Steamworks.Data.Connection> Connection;

            public PortableFunctionPointer<NetMsgHelper.FreeDelegate> FreeFn;
            
            public void Execute()
            {
                var count = SendQueue.Count;

                if ( count <= 0 )
                    return;
                
                var msgs = new UnsafePtrList<NetMsg>( count, Allocator.Temp, NativeArrayOptions.ClearMemory );
                var results = new NativeList<long>( count, Allocator.Temp );

                ProcessPacketProcessors( count, ref msgs );
                SendPackets( count, ref msgs, ref results );
                
                msgs.Dispose();
                results.Dispose();
            }

            private void ProcessPacketProcessors( int count, ref UnsafePtrList<NetMsg> msgs )
            {
                for ( var i = 0; i < count; i++ )
                {
                    var packetProcessor = SendQueue[i];

                    if ( packetProcessor.Length <= 0 )
                        continue;

                    void* data = (byte*)packetProcessor.GetUnsafePayloadPtr() + packetProcessor.Offset;

                    var destBuffer = UnsafeUtility.Malloc( packetProcessor.Length, UnsafeUtility.AlignOf<byte>(), Allocator.Persistent );

                    UnsafeUtility.MemCpy( destBuffer, data, packetProcessor.Length );

                    var netMsg = SteamNetworkingUtils.AllocateMessage();
                    
                    netMsg->Connection = Connection.Value;
                    netMsg->Flags = SendFlags;
                    netMsg->DataPtr = (IntPtr)destBuffer;
                    netMsg->DataSize = packetProcessor.Length;
                    netMsg->FreeDataPtr = FreeFn.Ptr.Value;

                    msgs.Add( netMsg );
                }
                
                SendQueue.Clear();
            }

            private void SendPackets( int count, ref UnsafePtrList<NetMsg> msgs, ref NativeList<long> results )
            {
                var msgPtr = msgs.Ptr;
                var resultsPtr = results.GetUnsafePtr();
                
                SteamNetworkingSockets.SendMessages_Client( msgs.Length, msgPtr, resultsPtr );
                
// #if ENABLE_SQUALIVE_NET_DEBUG
//                 for ( int i = 0; i < count; i++ )
//                 {
//                     Result result;
//                     if ( resultsPtr[i] < 0 )
//                     {
//                         result = (Result)( -resultsPtr[i] );
//                     }
//                     else
//                     {
//                         result = Result.OK;
//                     }
//                     
//                     Debug.Log($"Message Result: {result}");
//                 }
// #endif
            }
        }
        
        private unsafe struct ClientReceiveJob : IJob
        {
            public PacketsQueue ReceiveQueue;

            public OperationResult ReceiveResult;

            public NativeReference<Steamworks.Data.Connection> Connection;

            public int MaxMessageCount;
            
            public void Execute()
            {
                if ( !ReceiveQueue.EnqueuePacket( out var packetProcessor ) )
                {
                    ReceiveResult.ErrorCode = (int)StatusCode.NetworkReceiveQueueFull;
                    return;
                }
                
                int processed = 0;

                var buffer = new UnsafePtrList<NetMsg>( MaxMessageCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory );

                processed = SteamNetworkingSockets.ReceiveMessagesOnConnection_Client( Connection.Value, (IntPtr)buffer.Ptr, MaxMessageCount );

                if ( processed == 0 )
                {
                    ReceiveResult.ErrorCode = (int)StatusCode.Success;
                    
                    return;
                }
                
                for ( int i = 0; i < processed; i++ )
                {
                    var netMsg = buffer.Ptr[ i ];

                    packetProcessor.EndpointRef = netMsg->Identity.SteamId.ToNetworkEndpoint();
                    packetProcessor.AppendToPayload( (void*)netMsg->DataPtr, netMsg->DataSize );

                    NetMsg.InternalRelease( netMsg );
                }
                
                ReceiveResult.ErrorCode = (int)StatusCode.Success;
            }
        }

        public JobHandle ScheduleReceive( ref ReceiveJobArguments arguments, JobHandle dep )
        {
            if ( !IsValid )
            {
                return dep;
            }
            
            JobHandle jobHandle;

            if ( _parameters.IsServer.ToBoolean() )
            {
                var job = new ServerReceiveJob
                {
                    ReceiveQueue = arguments.ReceiveQueue,
                    ReceiveResult = arguments.ReceiveResult,
                    PollGroup = _pollGroup,
                    IsSteamServer = IsSteamServer,
                    MaxMessageCount = _parameters.MaxMessagePerUpdate,
                };

                jobHandle = job.Schedule( dep );
            }
            else
            {
                var job = new ClientReceiveJob
                {
                    ReceiveQueue = arguments.ReceiveQueue,
                    ReceiveResult = arguments.ReceiveResult,
                    Connection = _connection,
                    MaxMessageCount = _parameters.MaxMessagePerUpdate,
                };

                jobHandle = job.Schedule( dep );
            }
            
            return jobHandle;
        }

        public JobHandle ScheduleSend( ref SendJobArguments arguments, JobHandle dep )
        {
            if ( !IsValid )
            {
                return dep;
            }
            
            JobHandle jobHandle;
            
            if ( _parameters.IsServer.ToBoolean() )
            {
                var job = new ServerSendJob
                {
                    SendQueue = arguments.SendQueue,
                    Connections = _connected.AsReadOnly(),
                    FreeFn = NetMsgHelper.FreeFn,
                    IsSteamServer = IsSteamServer,
                };

                jobHandle = job.Schedule( dep );
            }
            else
            {
                var job = new ClientSendJob
                {
                    SendQueue = arguments.SendQueue,
                    Connection = _connection,
                    FreeFn = NetMsgHelper.FreeFn,
                };

                jobHandle = job.Schedule( dep );
            }

            return jobHandle;
        }

        public int Bind( NetworkEndpoint endpoint )
        {
            return _parameters.IsServer.ToBoolean() ? InitializeServer( endpoint ) : InitializeClient( endpoint );
        }

        public int Listen()
        {
            return _socket.Value != 0 ? 0 : (int)StatusCode.NetworkSocketError;
        }

        private int InitializeServer( NetworkEndpoint endpoint )
        {
            if ( _parameters.UsingRelay.ToBoolean() )
            {
                _socket.Value = IsSteamServer ? SteamNetworkingSockets.CreateRelaySocket_Server( _parameters.VirtualPort ) : SteamNetworkingSockets.CreateRelaySocket_Client( _parameters.VirtualPort );
            }
            else
            {
                var netAddress = endpoint.ToNetAddress();

                _socket.Value = IsSteamServer ? SteamNetworkingSockets.CreateNormalSocket_Server( netAddress ) : SteamNetworkingSockets.CreateNormalSocket_Client( netAddress );
            }

            _isValid.Value = true;
            
            return _socket.Value != 0 ? 0 : (int)StatusCode.NetworkSocketError;
        }

        private int InitializeClient( NetworkEndpoint endpoint )
        {
            if ( _parameters.UsingRelay.ToBoolean() )
            {
                _connection.Value = SteamNetworkingSockets.ConnectRelay_Client( endpoint.ToSteamId(), _parameters.VirtualPort );
            }
            else
            {
                var netAddress = endpoint.ToNetAddress();
                
                _connection.Value = SteamNetworkingSockets.ConnectNormal_Client( netAddress );
            }

            return _connection.Value != 0 ? 0 : (int)StatusCode.NetworkSocketError;
        }
        
        public void Dispose()
        {
            _isValid.Dispose();
            
            #region Server
            
            SteamNetworkingSockets.OnConnectionStatusChanged -= OnConnectionStatusChanged;

            if ( _parameters.IsServer.ToBoolean() )
            {
                if ( IsSteamServer )
                {
                    SteamNetworkingSockets.CloseListenSocket_Server( _socket.Value );
                    SteamNetworkingSockets.DestroyPollGroup_Server( _pollGroup.Value );
                }
                else
                {
                    SteamNetworkingSockets.CloseListenSocket_Client( _socket.Value );
                    SteamNetworkingSockets.DestroyPollGroup_Client( _pollGroup.Value );
                }

                _pollGroup.Dispose();
                _socket.Dispose();
                _connecting.Dispose();
                _connected.Dispose();
            }
            else
            {
                if ( IsSteamServer )
                {
                    SteamNetworkingSockets.CloseConnection_Server( _connection.Value );
                }
                else
                {
                    SteamNetworkingSockets.CloseConnection_Client( _connection.Value );
                }

                _connection.Dispose();
            }

            #endregion
        }

        private void OnConnectionStatusChanged( Steamworks.Data.Connection connection, ConnectionInfo connectionInfo )
        {
            var steamId = connectionInfo.Identity.SteamId;
            
            if ( connectionInfo.Socket.Id > 0 && _parameters.IsServer.ToBoolean() )
            {
                switch ( connectionInfo.State )
                {
                    case ConnectionState.Connected :
                        if ( _connecting.Contains( steamId ) && !_connected.ContainsKey( steamId ) )
                        {
                            _connecting.Remove( steamId );
                            _connected.Add( steamId, connection );

                            if ( IsSteamServer )
                            {
                                SteamNetworkingSockets.SetConnectionPollGroup_Server( connection, _pollGroup.Value );
                            }
                            else
                            {
                                SteamNetworkingSockets.SetConnectionPollGroup_Client( connection, _pollGroup.Value );
                            }
                        }
                        
                        break;
                    
                    case ConnectionState.Connecting:
                        if ( !_connecting.Contains( steamId ) && !_connected.ContainsKey( steamId ) )
                        {
                            _connecting.Add( steamId );

                            if ( IsSteamServer )
                            {
                                SteamNetworkingSockets.AcceptConnection_Server( connection );
                            }
                            else
                            {
                                SteamNetworkingSockets.AcceptConnection_Client( connection );
                            }
                        }
                        
                        break;
                    
                    case ConnectionState.ClosedByPeer:
                    case ConnectionState.Dead:
                    case ConnectionState.None:
                    case ConnectionState.ProblemDetectedLocally:
                        if ( _connecting.Contains( steamId ) || _connected.ContainsKey( steamId ) )
                        {
                            _connecting.Remove( steamId );
                            _connected.Remove( steamId );
                            
                            if ( IsSteamServer )
                            {
                                SteamNetworkingSockets.CloseConnection_Server( connection );
                            }
                            else
                            {
                                SteamNetworkingSockets.CloseConnection_Client( connection );
                            }
                        }
                        
                        
                        break;
                }
            }
            
            else if ( !_parameters.IsServer.ToBoolean() )
            {
                if ( connectionInfo.Identity.SteamId == SteamClient.SteamId )
                {
                    if ( connectionInfo.State == ConnectionState.Connected )
                    {
                        _isValid.Value = true;
                    }
                }
            }
        }
    }

    [BurstCompile]
    internal static unsafe class NetMsgHelper
    {
        internal static readonly PortableFunctionPointer<FreeDelegate> FreeFn = new( FreeNetMsg );
        
        [UnmanagedFunctionPointer( CallingConvention.Cdecl )]
        internal delegate void FreeDelegate( NetMsg* msg );
        
        [BurstCompile]
        [MonoPInvokeCallback(typeof(FreeDelegate))]
        private static void FreeNetMsg( NetMsg* msg )
        {
            UnsafeUtility.Free( msg, Allocator.Persistent );
        }
    }
}