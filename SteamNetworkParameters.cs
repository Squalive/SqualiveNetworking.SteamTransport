// This file is provided under The MIT License as part of SqualiveNetworking.
// Copyright (c) Squalive-Studios
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/Squalive/SqualiveNetworking

using System;
using SqualiveNetworking.Utils;
using Steamworks;
using Unity.Networking.Transport;

namespace SqualiveNetworking.SteamTransport
{
    public struct SteamNetworkParameters : INetworkParameter
    {
        public byte IsServer;
        
        public byte UsingRelay;

        public int VirtualPort;

        public int MaxMessagePerUpdate;
        
        public bool Validate()
        {
            return SteamClient.IsValid || SteamServer.IsValid;
        }
    }

    public static class SteamUtility
    {
        public static ref NetworkSettings WithSteamNetworkParameter( this ref NetworkSettings settings, bool isServer = true, bool usingRelay = false, int virtualPort = 0, int maxMessagePerUpdate = 32 )
        {
            if ( maxMessagePerUpdate < 1 || maxMessagePerUpdate > 128 )
                throw new ArgumentOutOfRangeException( nameof( maxMessagePerUpdate ) );
            
            var para = new SteamNetworkParameters
            {
                IsServer = isServer.ToByte(),
                UsingRelay = usingRelay.ToByte(),
                VirtualPort = virtualPort,
                MaxMessagePerUpdate = maxMessagePerUpdate,
            };

            settings.AddRawParameterStruct( ref para );

            return ref settings;
        }
    }
}