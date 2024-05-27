// This file is provided under The MIT License as part of SqualiveNetworking.
// Copyright (c) Squalive-Studios
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/Squalive/SqualiveNetworking

using Steamworks;
using Steamworks.Data;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Networking.Transport;

namespace SqualiveNetworking.SteamTransport.Utils
{
    public static unsafe class EndpointUtility
    {
        public static NetworkEndpoint ToNetworkEndpoint( this SteamId steamId )
        {
            NetworkEndpoint endpoint = default;
            
            *(SteamId*)&endpoint = steamId;

            endpoint.Family = NetworkFamily.Custom;

            return endpoint;
        }

        public static SteamId ToSteamId( this NetworkEndpoint endpoint )
        {
            return UnsafeUtility.As<NetworkEndpoint, SteamId>( ref endpoint );
        }
        
        public static NetworkEndpoint ToNetworkEndpoint( this NetAddress netAddress )
        {
            NetworkEndpoint endpoint = default;
            
            *(NetAddress*)&endpoint = netAddress;

            endpoint.Family = NetworkFamily.Custom;

            return endpoint;
        }

        public static NetAddress ToNetAddress( this NetworkEndpoint endpoint )
        {
            return UnsafeUtility.As<NetworkEndpoint, NetAddress>( ref endpoint );
        }
    }
}