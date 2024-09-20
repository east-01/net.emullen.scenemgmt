using System;
using System.Buffers.Text;
using System.Collections;
using System.Collections.Generic;
using EMullen.Core;
using FishNet;
using FishNet.Connection;
using FishNet.Managing.Scened;
using FishNet.Object;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace EMullen.SceneMgmt {
    /// <summary>
    /// The NetSceneController only exists when connected to the network.
    /// </summary>
    public class NetSceneController : NetworkBehaviour
    {

        public static NetSceneController Instance;
        public static bool IsReady => Instance != null && Instance.NetworkObject.IsSpawned;

        [SerializeField]
        private string initialGlobalScene = "";

        [SerializeField]
        private Dictionary<SceneLookupData, SceneElements> loadedScenes = new();
        [SerializeField]
        private List<SceneElements> loadedScenesList = new();

#region Initializers
        private void Awake() 
        {
            if(Instance != null)
                throw new InvalidOperationException("Tried to create a new NetSceneController when one already exists.");

            Instance = this;
        }

        private void OnEnable() 
        {
            InstanceFinder.SceneManager.OnLoadEnd += FishSceneManager_SceneLoaded; 
            InstanceFinder.SceneManager.OnUnloadEnd += FishSceneManager_SceneUnloaded;

            StartCoroutine(RefreshLoadedScenesListTask());
        }

        private void OnDisable() 
        {
            if(InstanceFinder.SceneManager != null) {
                InstanceFinder.SceneManager.OnLoadEnd -= FishSceneManager_SceneLoaded; 
                InstanceFinder.SceneManager.OnUnloadEnd -= FishSceneManager_SceneUnloaded;
            }
        }
#endregion

#region Scene Loading/Unloading
        [Server]
        public void LoadSceneAsServer(SceneLookupData lookupData) 
        {
            SceneLoadData sld = new SceneLoadData(lookupData);
            sld.Options.AllowStacking = true;
            sld.Options.AutomaticallyUnload = false;
            // if(CoreManager.IsMultiplayer)
            //     sld.Options.LocalPhysics = LocalPhysicsMode.Physics3D; // https://learn.unity.com/tutorial/multi-scene-physics?uv=2019.4#
            if(IsHostInitialized) {
                sld.ReplaceScenes = ReplaceOption.All;
                sld.PreferredActiveScene = new PreferredScene(lookupData);
            }

            SceneManager.LoadConnectionScenes(sld);
            BLog.Log($"Telling server to load scene w/ data name: {lookupData.Name} handle: {lookupData.Handle}", SceneController.Instance.logSettings, 0);
        }

        [Server]
        public void UnloadSceneAsServer(SceneLookupData lookupData) 
        {
            if(!IsHostInitialized)
                SceneController.Instance.NetSceneController_InvokeSceneWillDeregisterEvent(lookupData);

            SceneUnloadData sud = new(lookupData);
            SceneManager.UnloadConnectionScenes(sud);
            BLog.Log($"Telling server to unload scene w/ data name: {lookupData.Name} handle: {lookupData.Handle}", SceneController.Instance.logSettings, 0);
        } 

        /// <summary> See SceneDelegate#LoadScene </summary>
        [TargetRpc]
        public void TargetRpcLoadScene(NetworkConnection client, SceneLookupData lookupData, bool shouldTrack) => SceneController.Instance.LoadScene(lookupData, shouldTrack);
#endregion

#region Event Handlers
        /// <summary>
        /// The server side scene manager load event
        /// </summary>
        private void FishSceneManager_SceneLoaded(SceneLoadEndEventArgs args)
        {
            foreach(Scene scene in args.LoadedScenes) {
                BLog.Log($"{(IsServerInitialized ? "Server" : "Client")} loaded scene " + scene.name + ", handle: " + scene.handle, SceneController.Instance.logSettings, 0);
                RegisterScene(scene);
            }

            // Disable event systems
            if(IsServerInitialized && SceneController.Instance.StandaloneServer) {
                int disabledEventSystems = 0;
                foreach(EventSystem system in FindObjectsOfType<EventSystem>()) {
                    system.enabled = false;
                    disabledEventSystems++;
                }
                BLog.Log($"RegisterScenes disabled {disabledEventSystems} event system(s).", SceneController.Instance.logSettings, 0);
            }

            if(args.SkippedSceneNames.Length > 0)
                BLog.Log($"RegisterScenes skipped {args.SkippedSceneNames.Length} scene(s).", SceneController.Instance.logSettings, 0);
        }

        /// <summary>
        /// Server side scene unload event.
        /// </summary>
        private void FishSceneManager_SceneUnloaded(SceneUnloadEndEventArgs args) 
        {
            foreach(Scene scene in args.UnloadedScenes) {
                DeregisterScene(new(scene.handle, scene.name));
            }
        }
#endregion

#region Scene Registration
        /// <summary>
        /// Registers the scene in the SceneDelegate and issues a SceneRegisteredEvent when done.
        /// </summary>
        public void RegisterScene(Scene scene) 
        {
            SceneLookupData lookupData = new(scene.handle, scene.name);
            SceneElements elements = new() {
                Scene = scene
                // GameLobby owner is set by a GameLobby when the SceneRegistered event is called
                // GameplayManager is loaded by GameplayManagerDelegate when it's requested
            };

            loadedScenes.Add(lookupData, elements);

            BLog.Log($"Registered scene \"{lookupData}\". Calling event.", SceneController.Instance.logSettings, 0);
            SceneController.Instance.NetSceneController_InvokeSceneRegisteredEvent(lookupData);
        }

        public void DeregisterScene(SceneLookupData lookupData) 
        {
            if(!IsSceneRegistered(lookupData)) {
                Debug.LogWarning($"Failed to dereigster scene \"{lookupData}\". It is not registered.");
                return;
            }

            loadedScenes.Remove(lookupData);

            BLog.Log($"Deregistered scene \"{lookupData}\". Calling event.", SceneController.Instance.logSettings);
            SceneController.Instance.NetSceneController_InvokeSceneDeregisteredEvent(lookupData);
        }

        IEnumerator RefreshLoadedScenesListTask() 
        {
            while(gameObject.activeSelf) {
                RefreshLoadedScenesList();
                yield return new WaitForSeconds(1f);
            }
        }

        private void RefreshLoadedScenesList() 
        {
            loadedScenesList.Clear();
            foreach(SceneElements elements in loadedScenes.Values) {
                loadedScenesList.Add(elements);
            }
        }
#endregion

#region Client Movement
        /// <summary>
        /// Adds a client to a scene while removing them from other scenes they could be in.
        /// </summary>
        [Server]
        public void AddClientToScene(NetworkConnection client, SceneLookupData serverSceneLookupData) 
        {
            if(serverSceneLookupData == null) {
                Debug.LogError("Can't move client to scene because SceneLookupData is null.");
                return;
            }
            // If the scene isn't registered with the server, that means the client is moving to a non-server-tracked scene,
            //   all we should do is remove them from the scene they're in.
            if(!IsSceneRegistered(serverSceneLookupData)) {
                Debug.LogError($"Can't add client to scene \"{serverSceneLookupData}\" because it isn't registered.");
                return;
            }

            // Remove from existing scene
            if(GetClientScene(client) is not null)
                RemoveClientFromScene(client);

            TargetRpcEnsureSceneLoaded(client, serverSceneLookupData);
        }

        /// <summary>
        /// This internal side of AddClient to scene actually performs the SceneManager#AddConnectionToScene
        ///   method and perfomrs the event call
        /// </summary>
        [Server]
        private void Internal_AddClientToScene(NetworkConnection client, SceneLookupData serverSceneLookupData) 
        {
            if(!Internal_AddClientToSceneElements(client, serverSceneLookupData)) {
                Debug.LogError("Failed to add client to new scene.");
                return;
            }

            base.SceneManager.AddConnectionToScene(client, loadedScenes[serverSceneLookupData].Scene);
            TargetRpcClientAddedToScene(client, serverSceneLookupData);
        }

        [Server]
        public void RemoveClientFromScene(NetworkConnection client) 
        {
            SceneLookupData currentScene = GetClientScene(client);
            if(currentScene is null) {
                Debug.LogError("Can't remove client from scene since the clients current scene is null.");
                return;
            }

            if(!loadedScenes.ContainsKey(currentScene)) {
                Debug.LogError($"Can't remove client from scene \"{currentScene}\" because it's not loaded.");
                return;
            }

            if(!Internal_RemoveClientFromSceneElements(client, currentScene))  {
                Debug.LogError($"Failed to remove client from scene \"{currentScene}\"");
                return;
            }

            SceneElements elements = loadedScenes[currentScene];

            base.SceneManager.RemoveConnectionsFromScene(new NetworkConnection[] {client}, elements.Scene);

            if(elements.ClientCount == 0 && elements.DeleteOnLastClientRemove)
                UnloadSceneAsServer(currentScene);
        }

        [ServerRpc(RequireOwnership = false)]
        public void ServerRpcRemoveClientFromScene(NetworkConnection client) 
        {
            if(GetClientScene(client) is not null)
                RemoveClientFromScene(client);
        }

        /// <summary>
        /// Issue ClientAddedToSceneEvent to corresponding client.
        /// </summary>
        [TargetRpc]
        private void TargetRpcClientAddedToScene(NetworkConnection client, SceneLookupData lookup) 
        {
            SceneController.Instance.NetSceneController_InvokeClientAddedToSceneEvent(client, lookup);
        }

        /// <summary>
        /// Add a client to scene elements, returns success status.
        /// </summary>
        private bool Internal_AddClientToSceneElements(NetworkConnection client, SceneLookupData serverSceneLookupData) 
        {
            SceneElements elements = loadedScenes[serverSceneLookupData];
            if(elements.Clients.Contains(client)) {
                Debug.LogError($"Tried to add client to scene elements for scene \"{serverSceneLookupData}\" but they were already in the list!");
                return false;
            }

            elements.Clients.Add(client);
            loadedScenes[serverSceneLookupData] = elements;
            return true;
        }

        /// <summary>
        /// Removes a client from scene elements, returns success status.
        /// </summary>
        private bool Internal_RemoveClientFromSceneElements(NetworkConnection client, SceneLookupData serverSceneLookupData) 
        {
            SceneElements elements = loadedScenes[serverSceneLookupData];
            if(!elements.Clients.Contains(client)) {
                Debug.LogError($"Tried to remove client from scene elements for scene \"{serverSceneLookupData}\" but they weren't in the list!");
                return false;
            }

            elements.Clients.Remove(client);
            loadedScenes[serverSceneLookupData] = elements;
            return true;
        }
#endregion

#region Handshake
        /*NOTE: 
        During the handshake, we're passing around the SERVER SCENE LOOKUP DATA. This is
            an important distinction because the serverSceneLookupData has the reference for the
            corresponding SceneElements in the loadedScenes dictionary.
        The client only cares about the scene name since they'll load just a single scene,
            but the reference is important to keep. */

        /// <summary>
        /// Ensure that the specified SceneLookupData is loaded on the client.
        /// If the current scene name (on the client) matches the name in lookupData, we will skip
        ///   loading the scene and automatically call ServerRpcClientLoadedScene.
        /// If the current scene name does NOT match the name in lookup data, we will call
        ///   LoadSceneAsClient to load it.
        /// </summary>
        [TargetRpc]
        public void TargetRpcEnsureSceneLoaded(NetworkConnection client, SceneLookupData serverSceneLookupData) 
        {
            SceneController.Instance.clientLoadTarget = null;
            if(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != serverSceneLookupData.Name && !IsHostInitialized) {
                SceneController.Instance.LoadScene(serverSceneLookupData, true);
            } else {
                BLog.Log($"SceneDelegate#TargetRpcEnsureSceneLoaded: Scene \"{serverSceneLookupData.Name}\" is already loaded, skipping to SceneDelegate#ServerRpcClientLoadedScene", SceneController.Instance.logSettings, 0);
                ServerRpcClientLoadedScene(base.LocalConnection, serverSceneLookupData);
            }
        }

        /// <summary>
        /// Signal to the server that the client has loaded the specified scene.
        /// </summary>
        [ServerRpc(RequireOwnership = false)]
        public void ServerRpcClientLoadedScene(NetworkConnection client, SceneLookupData serverSceneLookupData) 
        {      
            if(serverSceneLookupData == null) {
                Debug.LogWarning("Client loaded scene call has null serverSceneLookupData.");
                return;      
            }
            // If the scene isn't registered with the server, that means the client is moving to a non-server-tracked scene,
            //   all we should do is remove them from the scene they're in.
            if(!IsSceneRegistered(serverSceneLookupData)) {
                SceneLookupData clientScene = GetClientScene(client);
                if(clientScene is not null)
                    Internal_RemoveClientFromSceneElements(client, clientScene);
                return;
            }

            BLog.Log($"Called ServerRPCClientLoadedScene for client {client} with data {serverSceneLookupData}", SceneController.Instance.logSettings, 3);
            Internal_AddClientToScene(client, serverSceneLookupData);
        }
#endregion

#region Getters/Setters
        public bool IsSceneRegistered(SceneLookupData lookupData) { return loadedScenes.ContainsKey(lookupData); }
        public SceneElements GetSceneElements(SceneLookupData lookupData) 
        {
            if(!loadedScenes.ContainsKey(lookupData)) {
                Debug.LogError($"Failed to get SceneElements for lookupData \"{lookupData}\". Put an IsSceneLoaded() check before the method calling this.");
                return default;
            }
            return loadedScenes[lookupData];     
        }

        public void SetSceneElements(SceneLookupData lookupData, SceneElements newElements) 
        {
            if(!loadedScenes.ContainsKey(lookupData)) {
                Debug.LogError($"Can't set scene elements for lookupData \"{lookupData}\". The scene must be loaded for you to set it's elements.");
                return;
            }
            if(loadedScenes[lookupData].Scene != newElements.Scene) {
                Debug.LogError($"Can't set scene elements, scene mismatch.");
                return;
            }
            loadedScenes[lookupData] = newElements;
        }

        /// <summary>
        /// Looks for the scene that the client is in.
        /// </summary>
        public SceneLookupData GetClientScene(NetworkConnection client) 
        {
            foreach(SceneLookupData lookupData in loadedScenes.Keys) {
                SceneElements elements = loadedScenes[lookupData];
                if(elements.Clients.Contains(client))
                    return lookupData;
            }
            return null;
        }
#endregion

        public void CheckInitialGlobalScene() 
        {
            if(!IsServerInitialized)
                return;
            if(!SceneController.Instance.StandaloneServer)
                return;
            if(initialGlobalScene == "") {
                Debug.LogWarning("No initial global scene provided. Will not enter one.");
                return;
            }

            // Ensure that the server makes its global scene MenuServer, that way we'll be able
            //   to load other maps/menus with stacking.
            Scene activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if(activeScene.name != initialGlobalScene) 
            {
                SceneLoadData sld = new SceneLoadData(initialGlobalScene);
                SceneManager.LoadConnectionScenes(base.LocalConnection, sld);

                SceneUnloadData sud = new SceneUnloadData(activeScene.name);
                SceneManager.UnloadGlobalScenes(sud);
            }
        }

        /// <summary>
        /// This really should only be used by CoreManager
        /// </summary>
        public static NetworkConnection GetLocalConnection() 
        {
            if(!IsReady) {
                Debug.LogError("Can't get local connection because the instance isn't ready.");
                return null;
            }
            return Instance.LocalConnection;
        }

    }
}