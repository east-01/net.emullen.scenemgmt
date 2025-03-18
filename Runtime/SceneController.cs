using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EMullen.Core;
using FishNet;
using FishNet.Connection;
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

        private List<Scene> serverScenes = new(); 
        /// <summary>
        /// A dictionary containing the client and the scene lookup data that the client is loading
        ///   and waiting to join.
        /// Will be watched in Update() and wait for clients to load their target scenes to add
        ///   them into it.
        /// </summary>
        private Dictionary<NetworkConnection, SceneLookupData> clientsLoadingScenes = new();

        /// <summary>
        /// For the server: A dictionary containing the client connections and that client's list
        ///   of loaded scenes.
        /// </summary>
        private Dictionary<NetworkConnection, List<SceneLookupData>> loadedScenes = new();
        /// <summary>
        /// For the server: A dictionary containing the client connections and that client's list
        ///   of target scenes.
        /// </summary>
        private Dictionary<NetworkConnection, List<SceneLookupData>> targetScenes = new();

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

        public delegate void ServerSceneListChangeHandler(SceneLookupData changedData, List<Scene> changedList, bool added);
        /// <summary>
        /// Event call signaling on the server that the scene list has changed, server side only.
        /// </summary>
        public event ServerSceneListChangeHandler ServerSceneListChangeEvent;
        public delegate void ClientNetworkedSceneHandler(NetworkConnection client, SceneLookupData scene, ClientNetworkedScene.Action action);
        /// <summary>
        /// Event call signaling a client has made a network interaction with a scene, server
        ///   side only.
        /// </summary>
        public event ClientNetworkedSceneHandler ClientNetworkedSceneEvent;

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

            InstanceFinder.SceneManager.OnLoadEnd += FishSceneManager_LoadEnd;

            InstanceFinder.ClientManager.RegisterBroadcast<SceneSyncBroadcast>(OnSceneSync);
            InstanceFinder.ClientManager.RegisterBroadcast<ClientNetworkedScene>(OnClientNetworkedScene);
            InstanceFinder.ServerManager.RegisterBroadcast<ClientSceneChangeBroadcast>(OnClientSceneChange);
            InstanceFinder.ServerManager.RegisterBroadcast<ClientNetworkedScene>(OnClientRequestNetworkedScene);
        }

        private void OnDisable() 
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= UnitySceneManager_SceneLoaded;
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded -= UnitySceneManager_SceneUnloaded;

            InstanceFinder.ClientManager.UnregisterBroadcast<SceneSyncBroadcast>(OnSceneSync);
            InstanceFinder.ClientManager.UnregisterBroadcast<ClientNetworkedScene>(OnClientNetworkedScene);
            InstanceFinder.ServerManager.UnregisterBroadcast<ClientSceneChangeBroadcast>(OnClientSceneChange);
            InstanceFinder.ServerManager.UnregisterBroadcast<ClientNetworkedScene>(OnClientRequestNetworkedScene);
        }
#endregion

        private void Update() 
        {
            foreach(NetworkConnection client in new List<NetworkConnection>(clientsLoadingScenes.Keys)) {
                // BLog.Highlight("Searching for " + clientsLoadingScenes[client] + " in loaded scenes: " + string.Join(", ", loadedScenes[client]));
                // Attempt to add the client to the scene
                AddClientToScene(client, clientsLoadingScenes[client]);
            }
        }

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
            if(!InstanceFinder.IsClientStarted)
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
            if(!InstanceFinder.IsClientStarted)
                return;

            BLog.Log($"SceneController unloaded \"{scene.name}\". Signaling to server.", logSettings, 0);
            ClientSceneChangeBroadcast broadcast = ClientSceneChangeBroadcast.UnloadBroadcastFactory(LoadedScenes, scene.GetSceneLookupData());
            InstanceFinder.ClientManager.Broadcast(broadcast);
        }

        private void FishSceneManager_LoadEnd(SceneLoadEndEventArgs args)
        {
            foreach(Scene scene in args.LoadedScenes) {
                if(serverScenes.Contains(scene)) 
                    Debug.LogWarning("Fishnet loaded a scene even though it's already in the server scenes list.");

                serverScenes.Add(scene);
                ServerSceneListChangeEvent?.Invoke(scene.GetSceneLookupData(), serverScenes, true);
                BLog.Log($"FishNet SceneManager loaded scene {scene.GetSceneLookupData()}");
            }
        }

        /// <summary>
        /// Method call coming from each client to indicate a scene has changed.
        /// </summary>
        private void OnClientSceneChange(NetworkConnection client, ClientSceneChangeBroadcast msg, Channel channel) 
        {
            bool changed = false;
            if(!loadedScenes.ContainsKey(client)) {
                loadedScenes.Add(client, msg.scenes);
                changed = true;
            } else {
                changed = !loadedScenes[client].Equals(msg.scenes);
                loadedScenes[client] = msg.scenes;
            }

            if(!changed)
                return;

            if(msg.cause == ClientSceneChangeBroadcast.Cause.LOAD) {
                // Ensure the client is in the target scenes list.
                if(!targetScenes.ContainsKey(client)) {
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
        /// Method call coming from the server to indicate to the client that they were 
        ///   added/removed from a networked scene.
        /// </summary>
        /// <param name="scene">The scene they were</param>
        /// <param name="channel"></param>
        /// <exception cref="NotImplementedException"></exception>
        private void OnClientNetworkedScene(ClientNetworkedScene msg, Channel channel)
        {
            if(msg.action == ClientNetworkedScene.Action.ADD)
                UnityEngine.SceneManagement.SceneManager.SetActiveScene(UnityEngine.SceneManagement.SceneManager.GetSceneByName(msg.scene.Name));
        }

        /// <summary>
        /// Method call coming from a client to request a networked scene action.
        /// </summary>
        /// <param name="client">The client requesting the action</param>
        /// <param name="msg"></param>
        /// <param name="channel"></param>
        private void OnClientRequestNetworkedScene(NetworkConnection client, ClientNetworkedScene msg, Channel channel) 
        {
            if(msg.action == ClientNetworkedScene.Action.ADD) {
                AddClientToScene(client, msg.scene);
            } else {
                Debug.LogError("TODO: Implement RemoveClientFromScene");
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

#region Server Scene Management
        public void LoadServerScene(SceneLookupData lookupData) 
        {
            SceneLoadData sld = new(lookupData);
            InstanceFinder.SceneManager.LoadConnectionScenes(sld);
        }

        public void AddClientToScene(NetworkConnection client, SceneLookupData sceneLookupData) 
        {
            if(!InstanceFinder.IsServerStarted) {
                ServerRpcAddClientToScene(client, sceneLookupData);
                return;
            }
            if(!client.IsValid) {
                Debug.LogError("Can't add client to scene, client isn't valid!");
                return;
            }

            bool isSceneLoaded = false;
            if(sceneLookupData.Handle != 0)
                isSceneLoaded = serverScenes.Select(scene => scene.GetSceneLookupData()).Contains(sceneLookupData);
            else if(serverScenes.Any(scene => scene.GetSceneLookupData().Name == sceneLookupData.Name)) {
                sceneLookupData = serverScenes.First(scene => scene.GetSceneLookupData().Name == sceneLookupData.Name).GetSceneLookupData();
                isSceneLoaded = sceneLookupData.IsValid;
            }

            // If the scene isn't loaded they need to be put in the loading queue.
            if(!isSceneLoaded) {
                if(!clientsLoadingScenes.ContainsKey(client)) {
                    clientsLoadingScenes.Add(client, sceneLookupData);
                }
                return;
            }
            
            // In case this client was in the queue when this call was made, remove them
            if(clientsLoadingScenes.ContainsKey(client) && isSceneLoaded) {
                clientsLoadingScenes.Remove(client);
            }

            Scene scene = serverScenes.First(scene => scene.GetSceneLookupData() == sceneLookupData);
            InstanceFinder.SceneManager.AddConnectionToScene(client, scene);
            ClientNetworkedScene broadcast = new(scene.GetSceneLookupData(), ClientNetworkedScene.Action.ADD);
            InstanceFinder.ServerManager.Broadcast(client, broadcast);
            ClientNetworkedSceneEvent?.Invoke(client, scene.GetSceneLookupData(), ClientNetworkedScene.Action.ADD);
        }

        private void ServerRpcAddClientToScene(NetworkConnection client, SceneLookupData sceneLookupData) => AddClientToScene(client, sceneLookupData);
#endregion

    }

    public static class SceneControllerExtensions 
    {
        public static SceneLookupData GetSceneLookupData(this Scene scene) => new(scene.handle, scene.name);
    }
}