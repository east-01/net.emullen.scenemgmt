using System.Collections.Generic;
using FishNet.Broadcast;
using FishNet.Connection;
using FishNet.Managing.Scened;

namespace EMullen.SceneMgmt 
{
    public struct SceneSetBroadcast : IBroadcast 
    {
        public NetworkConnection conn;
        public List<SceneLookupData> scenes;
    }
}