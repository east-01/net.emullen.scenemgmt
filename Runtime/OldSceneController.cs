using System;
using System.Collections;
using System.Collections.Generic;
using EMullen.Core;
using FishNet;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Managing.Scened;
using FishNet.Object;
using FishNet.Transporting;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace EMullen.SceneMgmt {
    /// <summary>
    /// The Scene Controller is a core level object that manages scenes, it also interfaces
    ///   with the NetSceneController (when connected to the network) to allow for server-side
    ///   scene actions.
    /// It essentially acts as the client side for the NetSceneController.
    /// Events are called from here, so we'll be able to trust that the SceneController's events
    ///   we subscribe to will stay the same even if the network changes.
    /// </summary>
    public class OldSceneController : MonoBehaviour
    {

        public static OldSceneController Instance;

        public BLogChannel logSettings;

        public bool StandaloneServer;
        /// <summary>
        /// Client side only, the data that we're trying to get the client to load
        /// </summary>
        public SceneLookupData clientLoadTarget;

#if FISHNET
        [SerializeField]
        private GameObject NetworkedSceneControllerPrefab;

        private NetworkManager networkManager;
#endif

#region Events
        public delegate void SceneRegisteredHandler(SceneLookupData sceneLookupData); // We don't provide SceneElements here to require users of event to go through SceneDelegate
        /// <summary>
        /// Called when a scene is registered with the SceneDelegate
        /// </summary>
        public event SceneRegisteredHandler SceneRegisteredEvent;
        /// <summary>SHOULD ONLY BE USED BY NetSceneController!! Will issue SceneController events.</summary>
        internal void NetSceneController_InvokeSceneRegisteredEvent(SceneLookupData sceneLookupData) { SceneRegisteredEvent?.Invoke(sceneLookupData); }

        public delegate void SceneWillUnloadHandler(string unloadingScene, string loadingScene);
        /// <summary>
        /// Called before the UnitySceneManager loads a new Scene.
        /// </summary>
        public event SceneWillUnloadHandler SceneWillUnloadEvent;

        public delegate void SceneWillDeregisterHandler(SceneLookupData sceneLookupData); // No SceneElements here, see scene registered handler
        /// <summary>
        /// Called when a scene is told to unload on the server but before the unload actually happens.
        /// In place to allow things in the scene to wrap up properly.
        /// </summary>
        public event SceneWillDeregisterHandler SceneWillDeregisterEvent;
        /// <summary>SHOULD ONLY BE USED BY NetSceneController!! Will issue SceneController events.</summary>
        internal void NetSceneController_InvokeSceneWillDeregisterEvent(SceneLookupData sceneLookupData) { SceneWillDeregisterEvent?.Invoke(sceneLookupData); }

        public delegate void SceneDeregisteredHandler(SceneLookupData sceneLookupData); // No SceneElements here, see scene registered handler
        /// <summary>
        /// Called when a scene is deregistered with the scene delegate;
        /// </summary>
        public event SceneDeregisteredHandler SceneDeregisteredEvent;
        /// <summary>SHOULD ONLY BE USED BY NetSceneController!! Will issue SceneController events.</summary>
        internal void NetSceneController_InvokeSceneDeregisteredEvent(SceneLookupData sceneLookupData) { SceneDeregisteredEvent?.Invoke(sceneLookupData); }

        public delegate void ClientAddedToSceneHandler(NetworkConnection client, SceneLookupData sceneLookupData);
        /// <summary>
        /// Called when a client is added to the scene.
        /// For now, only is called on the client that was added.
        /// </summary>
        public event ClientAddedToSceneHandler ClientAddedToSceneEvent;
        /// <summary>SHOULD ONLY BE USED BY NetSceneController!! Will issue SceneController events.</summary>
        internal void NetSceneController_InvokeClientAddedToSceneEvent(NetworkConnection client, SceneLookupData sceneLookupData) { ClientAddedToSceneEvent?.Invoke(client, sceneLookupData); }
#endregion

#region Initializers
        private void Awake() 
        {
            BLog.Log("SceneController woke up", logSettings, 0);
            if(Instance != null)
                throw new InvalidOperationException("Tried to create a new SceneDelegate when one already exists.");

            Instance = this;
            DontDestroyOnLoad(this);

#if FISHNET
            SubscribeToNetworkEvents();
#endif
        }

        private void Update() 
        {
#if FISHNET
            SubscribeToNetworkEvents();
#endif
        }

        private void OnDestroy() 
        {
#if FISHNET
            UnsubscribeFromNetworkEvents();
#endif
        }

        private void OnEnable() 
        { 
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += UnitySceneManager_SceneLoaded;
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded += UnitySceneManager_SceneUnloaded;

            InstanceFinder.ClientManager.RegisterBroadcast<SceneSetBroadcast>(RecieveClientSceneSetBroadcast);
            InstanceFinder.ServerManager.RegisterBroadcast<SceneSetBroadcast>(RecieveServerSceneSetBroadcast);
        }

        private void OnDisable() { 
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= UnitySceneManager_SceneLoaded;
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded -= UnitySceneManager_SceneUnloaded;

            InstanceFinder.ClientManager.UnregisterBroadcast<SceneSetBroadcast>(RecieveClientSceneSetBroadcast);
            InstanceFinder.ServerManager.UnregisterBroadcast<SceneSetBroadcast>(RecieveServerSceneSetBroadcast);
        }
#endregion

#region Scene Loading
        /// <summary>
        /// Load a scene for the client using the UnityEngine SceneManager.
        /// Will only be tracked by the SceneManager if shouldTrack = true
        /// </summary>
        /// <param name="shouldTrack">Track the scene in the scene manager</param>
        public void LoadScene(SceneLookupData lookupData, bool shouldTrack) 
        {
            // Call will deregister event for the active scene
            BLog.Log($"Loading scene \"{lookupData}\" as client, shouldTrack: {shouldTrack}", logSettings, 0);
            if(NetSceneController.IsReady)
                BLog.Log($"LoadSceneAsClient: Is active scene \"{ActiveSceneLookupData}\" registered: {NetSceneController.Instance.IsSceneRegistered(ActiveSceneLookupData)}", logSettings, 2);
            if(NetSceneController.IsReady && NetSceneController.Instance.IsSceneRegistered(ActiveSceneLookupData))
                SceneWillDeregisterEvent?.Invoke(ActiveSceneLookupData);

            clientLoadTarget = shouldTrack ? lookupData : null;

            SceneWillUnloadEvent?.Invoke(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name, lookupData.Name);
            UnityEngine.SceneManagement.SceneManager.LoadScene(lookupData.Name, LoadSceneMode.Single);
        }
#endregion

#region Event Handlers
        /// <summary>
        /// The client side scene manager load event
        /// </summary>
        private void UnitySceneManager_SceneLoaded(Scene scene, LoadSceneMode loadSceneMode)  
        {
            BLog.Highlight("!!!!");
            // We don't care about the server side of this event
            if(InstanceFinder.IsServerOnlyStarted)
                return;
            if(!NetSceneController.IsReady)
                return;

            BLog.Log("LoadedScenes#UnitySceneManager_SceneLoaded: Validated client loaded, scene. Disconnecting them from their other scenes.", logSettings, 0);
            NetSceneController.Instance.ServerRpcRemoveClientFromScene(NetSceneController.Instance.LocalConnection);

            // If the client load target is null, we're not tracking this scene load
            if(clientLoadTarget == null)
                return;

            if(scene != null && clientLoadTarget is not null && scene.name != clientLoadTarget.Name) {
                Debug.LogWarning("Scene load didn't match load target.");
                return;
            }
            BLog.Highlight("!!!!asdasda");

            // Only register the scene if we're not in a local instance, this is because the FishNet register method will do it
            if(StandaloneServer)
                NetSceneController.Instance.RegisterScene(scene);

            BLog.Log($"SceneDelegate#UnitySceneManager_SceneLoaded: Client loaded scene \"{scene.name}\"", logSettings, 0);
            NetSceneController.Instance.ClientLoadedScene(NetSceneController.Instance.LocalConnection, clientLoadTarget);
        }

        private void UnitySceneManager_SceneUnloaded(Scene scene) 
        {
            if(!NetSceneController.IsReady)
                return;
            SceneLookupData lookupData = new(scene.handle, scene.name);
            if(!NetSceneController.Instance.IsSceneRegistered(lookupData))
                return;
            NetSceneController.Instance.DeregisterScene(lookupData);
        }

        private void RecieveClientSceneSetBroadcast(SceneSetBroadcast msg, Channel channel) 
        {

        }

        private void RecieveServerSceneSetBroadcast(NetworkConnection conn, SceneSetBroadcast msg, Channel channel) 
        {
            
        }
#endregion

#region Networked scene controller
        private void SubscribeToNetworkEvents() 
        {
            if(networkManager != null)
                return;

            networkManager = InstanceFinder.NetworkManager;
            networkManager.ServerManager.OnServerConnectionState += ServerManager_OnServerConnectionState;

            BLog.Log("Subscribed to network events.", logSettings, 1);
        }

        private void UnsubscribeFromNetworkEvents() 
        {
            if(networkManager == null)
                return;

            networkManager.ServerManager.OnServerConnectionState += ServerManager_OnServerConnectionState;
            networkManager = null;
        }

        private void ServerManager_OnServerConnectionState(ServerConnectionStateArgs args)
        {
            if(args.ConnectionState == LocalConnectionState.Started) {
                GameObject instantiated = Instantiate(NetworkedSceneControllerPrefab);
                InstanceFinder.ServerManager.Spawn(instantiated.GetComponent<NetworkObject>());
            }
        }
#endregion

        private SceneLookupData ActiveSceneLookupData { get { 
            Scene active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            return new(active.handle, active.name);
        } }

    }
}