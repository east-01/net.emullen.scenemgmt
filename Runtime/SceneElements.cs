using System;
using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Managing.Scened;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EMullen.SceneMgmt 
{
        /// <summary>
    /// To be paired with SceneLookupData to hold relevant scene elements in the SceneDelegate
    /// </summary>
    [Serializable]
    public struct SceneElements {
        [SerializeField]
        private SceneLookupData lookupData;

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

        // [SerializeField]
        // private GameLobby owner;
        // /// <summary>
        // /// Only usable on the server side. Can be null if no lobby claims.
        // /// </summary>
        // public GameLobby Owner {
        //     readonly get { return owner; }
        //     set {
        //         if(owner != null) {
        //             Debug.LogError("Can't set owner since one already exists.");
        //             return;
        //         }
        //         owner = value;
        //     }
        // }
        // public bool HasOwner { get { return owner != null; } }

        // [SerializeField]
        // private GameplayManager gameplayManager;
        // /// <summary>
        // /// The GameplayManager held in this scene. Can be null if a lobby scene.
        // /// </summary>
        // public GameplayManager GameplayManager {
        //     get {
        //         if(gameplayManager == null) {
        //             gameplayManager = GameplayManagerDelegate.LocateGameplayManager(scene);
        //         }
        //         return gameplayManager;
        //     }
        // }

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