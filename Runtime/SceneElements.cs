using System;
using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Managing.Scened;
using UnityEngine;
using UnityEngine.SceneManagement;
using Newtonsoft.Json.Linq;

namespace EMullen.SceneMgmt 
{
    /// <summary>
    /// To be paired with SceneLookupData to hold relevant scene elements in the SceneDelegate
    /// </summary>
    [Serializable]
    public struct SceneElements {
        [SerializeField]
        private SceneLookupData lookupData;
        public readonly SceneLookupData LookupData => lookupData;

        private Scene scene;
        /// <summary>
        /// The scene reference
        /// </summary>
        public Scene Scene {
            readonly get { return scene; } 
            set { 
                if(scene.IsValid()) {
                    Debug.LogError("Can't overwrite existing scene.");
                    return;
                }
                scene = value;
                lookupData = new(value.handle, value.name);
            }
        }

        [SerializeField]
        private List<NetworkConnection> clients;
        /// <summary>
        /// A list of clients in the scene
        /// </summary>
        public List<NetworkConnection> Clients {
            get {
                clients ??= new();
                return clients;
            }
        }
        [SerializeField]
        private int clientCount; // Exposed for serialization in editor
        /// <summary>
        /// The amount of clients in the scene
        /// </summary>
        public int ClientCount { get { 
            clientCount = Clients.Count;
            return clientCount; 
        } }

        /// <summary>
        /// When true, the SceneDelegate will delete the scene when the last player is removed from
        ///   the scene.
        /// </summary>
        public bool DeleteOnLastClientRemove;

    }
}