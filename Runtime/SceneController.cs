using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EMullen.Core;
using FishNet;
using FishNet.Connection;
using FishNet.Managing.Scened;
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
    public class SceneController : MonoBehaviour
    {

        public static SceneController Instance;

        public BLogChannel logSettings;

        private SceneLookupData ActiveSceneLookupData => UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetSceneLookupData();
        private List<SceneLookupData> LoadedScenes => BuildProcessor.Scenes.ToList()
        .Where(bss => {
            Scene scene = UnityEngine.SceneManagement.SceneManager.GetSceneByBuildIndex(bss.index);
            return bss.enabled && scene.isLoaded;
        }).Select(bss => {
            return UnityEngine.SceneManagement.SceneManager.GetSceneByBuildIndex(bss.index).GetSceneLookupData();
        }).ToList();

        /// <summary>
        /// For the server: A dictionary containing the client connections and that client's list
        ///   of target scenes.
        /// </summary>
        private Dictionary<NetworkConnection, List<SceneLookupData>> targetScenes;

        /// <summary>
        /// For clients: The target list of scene lookup datas as specified by the server from a 
        ///   SceneSyncBroadcast.
        /// </summary>
        private List<SceneLookupData> clientTargetScenes;

        public delegate void LoadedTargetScenesHandler(List<SceneLookupData> loadedScenes, NetworkConnection connection = null);
        /// <summary>
        /// Event call signaling a client has loaded their target scenes.
        /// Comes with the list of SceneLookupDatas that we're loaded and, if running as the
        ///   server, a NetworkConnection indicating who loaded what scene.
        /// </summary>
        public event LoadedTargetScenesHandler LoadedTargetScenes;

#region Initializers
        private void Awake() 
        {
            if(Instance != null)
                throw new InvalidOperationException("Tried to create a new SceneDelegate when one already exists.");

            Instance = this;
            DontDestroyOnLoad(this);

        }
        
        private void OnEnable() 
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += UnitySceneManager_SceneLoaded;
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded += UnitySceneManager_SceneUnloaded;

            InstanceFinder.ClientManager.RegisterBroadcast<SceneSyncBroadcast>(OnSceneSync);
            InstanceFinder.ServerManager.RegisterBroadcast<ClientSceneChangeBroadcast>(OnClientSceneChange);
        }

        private void OnDisable() 
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= UnitySceneManager_SceneLoaded;
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded -= UnitySceneManager_SceneUnloaded;

            InstanceFinder.ClientManager.UnregisterBroadcast<SceneSyncBroadcast>(OnSceneSync);
            InstanceFinder.ServerManager.UnregisterBroadcast<ClientSceneChangeBroadcast>(OnClientSceneChange);
        }
#endregion

        /// <summary>
        /// Server only.
        /// Tell the connection to load a specific set of scenes. This will store the target scene
        ///   list on the server in targetScenes.
        /// </summary>
        /// <param name="sceneLoadSet">The </param>
        public void LoadScenesOnConnection(NetworkConnection conn, List<SceneLookupData> sceneLoadSet) 
        {
            if(!InstanceFinder.IsServerStarted)
                throw new InvalidOperationException("Can't load scenes for client. Server isn't started.");

            SceneSyncBroadcast broadcast = new(sceneLoadSet);
            InstanceFinder.ServerManager.Broadcast(conn, broadcast);
        }

#region Event Handlers
        /// <summary>
        /// Unity scene manager scene loaded event callback. Used by clients to notify the server
        ///   of local scene changes.
        /// </summary>
        private void UnitySceneManager_SceneLoaded(Scene scene, LoadSceneMode loadSceneMode)  
        {
            // We're only synchronizing the clients.
            if(InstanceFinder.IsClientStarted)
                return;

            BLog.Log($"SceneController loaded \"{scene.name}\". Signaling to server.", logSettings, 0);
            ClientSceneChangeBroadcast broadcast = ClientSceneChangeBroadcast.LoadBroadcastFactory(LoadedScenes, scene.GetSceneLookupData(), loadSceneMode);
            InstanceFinder.ClientManager.Broadcast(broadcast);

            if(LoadedScenes != null && clientTargetScenes != null && LoadedScenes.ToHashSet().SetEquals(clientTargetScenes.ToHashSet())) {
                LoadedTargetScenes?.Invoke(clientTargetScenes);
                clientTargetScenes = null;
                BLog.Log("Loaded target scenes locally.", logSettings, 1);
            }
        }

        private void UnitySceneManager_SceneUnloaded(Scene scene) 
        {
            // We're only synchronizing the clients.
            if(InstanceFinder.IsClientStarted)
                return;

            BLog.Log($"SceneController unloaded \"{scene.name}\". Signaling to server.", logSettings, 0);
            ClientSceneChangeBroadcast broadcast = ClientSceneChangeBroadcast.UnloadBroadcastFactory(LoadedScenes, scene.GetSceneLookupData());
            InstanceFinder.ClientManager.Broadcast(broadcast);
        }

        /// <summary>
        /// Method call coming from each client to indicate a scene has changed.
        /// </summary>
        private void OnClientSceneChange(NetworkConnection client, ClientSceneChangeBroadcast msg, Channel channel) 
        {
            if(msg.cause == ClientSceneChangeBroadcast.Cause.LOAD) {
                // Ensure the client is in the target scenes list.
                if(!targetScenes.ContainsKey(client)) {
                    Debug.LogWarning("Can't check if client loaded target scenes, they don't have any target scenes.");
                    return;
                }

                HashSet<SceneLookupData> updatedSceneSet = msg.scenes.ToHashSet();
                HashSet<SceneLookupData> targetSet = targetScenes[client].ToHashSet();

                if(updatedSceneSet.SetEquals(targetSet)) {
                    LoadedTargetScenes?.Invoke(targetScenes[client], client);
                    BLog.Log($"Client id \"{client.ClientId}\" loaded target scene set.", logSettings, 1);
                }
            }
        }

        /// <summary>
        /// Method call coming from the server to indicate to a client what set of scenes they
        ///   should have loaded.
        /// Will set targetScenes array to the scene set from the server.
        /// </summary>
        private void OnSceneSync(SceneSyncBroadcast broadcast, Channel channel)
        {
            // Check if the client scene list is set and warn if so, the client scene list is
            //   cleared once it loads all target scenes.
            if(clientTargetScenes != null)
                Debug.LogWarning("Recieved a SceneSyncBroadcast even though we're still loading scenes!");

            clientTargetScenes = broadcast.scenes;

            for(int i = 0; i < clientTargetScenes.Count; i++) {
                // The first scene should be loaded as single, the following scenes should be additive.
                LoadSceneMode mode = i == 0 ? LoadSceneMode.Single : LoadSceneMode.Additive;
                SceneLookupData sld = clientTargetScenes[i];
                UnityEngine.SceneManagement.SceneManager.LoadScene(sld.Name, mode);
            }

            BLog.Log("Recieved scene sync broadcast, loading scenes", logSettings, 1);
        }
#endregion

    }

    public static class SceneControllerExtensions 
    {
        public static SceneLookupData GetSceneLookupData(this Scene scene) => new(scene.handle, scene.name);
    }
}