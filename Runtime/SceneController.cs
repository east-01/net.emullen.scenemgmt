using System;
using System.Collections.Generic;
using System.Linq;
using EMullen.Core;
using FishNet;
using FishNet.Connection;
using FishNet.Managing.Scened;
using FishNet.Transporting;
using UnityEngine;
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
    public partial class SceneController : MonoBehaviour
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
        /// For the server: A dictionary containing the client connections and that client's list
        ///   of loaded scenes.
        /// </summary>
        private Dictionary<NetworkConnection, List<SceneLookupData>> loadedScenes = new();
        /// <summary>
        /// For the server: A dictionary containing the client connections and that client's list
        ///   of target scenes.
        /// </summary>
        private Dictionary<NetworkConnection, List<SceneLookupData>> targetScenes = new();

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
        public delegate void ClientNetworkedSceneHandler(NetworkConnection client, SceneLookupData scene, ClientNetworkedSceneAction action);
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
            ClientOnEnable();

            InstanceFinder.SceneManager.OnLoadEnd += FishSceneManager_LoadEnd;

            InstanceFinder.ServerManager.RegisterBroadcast<ClientSceneChangeBroadcast>(OnClientSceneChange);
            InstanceFinder.ServerManager.RegisterBroadcast<ClientNetworkedScene>(OnClientRequestNetworkedScene);
        }

        private void OnDisable() 
        {
            ClientOnDisable();

            InstanceFinder.SceneManager.OnLoadEnd -= FishSceneManager_LoadEnd;

            InstanceFinder.ServerManager.UnregisterBroadcast<ClientSceneChangeBroadcast>(OnClientSceneChange);
            InstanceFinder.ServerManager.UnregisterBroadcast<ClientNetworkedScene>(OnClientRequestNetworkedScene);
        }
#endregion

        private void Update() 
        {
            Dictionary<Scene, HashSet<NetworkConnection>> sceneConnections = InstanceFinder.SceneManager.SceneConnections;

            // Try to add clients to their networked scenes.
            foreach(NetworkConnection client in new List<NetworkConnection>(targetScenes.Keys)) {
                if(!targetScenes.ContainsKey(client))
                    continue;

                HashSet<SceneLookupData> loadedSet = loadedScenes[client].ToHashSet();
                HashSet<SceneLookupData> targetSet = targetScenes[client].ToHashSet();

                IEnumerable<SceneLookupData> loadedTargets = loadedSet.Intersect(targetSet);
                foreach(SceneLookupData loadedTarget in loadedTargets) {
                    Scene? sceneNullable = sceneConnections.Keys.Search(loadedTarget);
                    if(!sceneNullable.HasValue) {
                        // TODO: Warning to handle this case
                        Debug.LogWarning("LoadedTarget scene is null, this shouldn't happen unless a scene unloads.");
                        continue;
                    }
                    
                    Scene scene = sceneNullable.Value;
                    if(!sceneConnections[scene].Contains(client))
                        AddClientToScene(client, scene.GetSceneLookupData());
                }

            }
        }

        public void LoadServerScene(SceneLookupData lookupData) 
        {
            SceneLoadData sld = new(lookupData);

            if(InstanceFinder.IsHostStarted) {
                sld.ReplaceScenes = ReplaceOption.All;
            }

            InstanceFinder.SceneManager.LoadConnectionScenes(sld);
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

            BLog.Highlight($"Telling conn {conn} to load scenes: {string.Join(", ", sceneLoadSet)}");

            if(targetScenes.ContainsKey(conn)) {
                Debug.LogWarning($"Overriding loading scenes on connection {conn}! Was \"{string.Join(", ", targetScenes[conn])}\" is now \"{string.Join(", ", sceneLoadSet)}\"");
                targetScenes.Remove(conn);
            }

            targetScenes.Add(conn, sceneLoadSet);

            SceneSyncBroadcast broadcast = new(sceneLoadSet);
            InstanceFinder.ServerManager.Broadcast(conn, broadcast);
        }

        public void AddClientToScene(NetworkConnection client, SceneLookupData sceneLookupData) 
        {
            if(!InstanceFinder.IsServerStarted)
                throw new InvalidOperationException("Can't add client to scene, server isn't started.");

            if(!client.IsValid) {
                Debug.LogError("Can't add client to scene, client isn't valid!");
                return;
            }

            Scene? sceneNullable = serverScenes.Search(sceneLookupData, true);
            if(!sceneNullable.HasValue)
                throw new InvalidOperationException($"Can't add client to scene \"{sceneLookupData}\" it is not loaded in server scenes.");

            Scene scene = sceneNullable.Value;

            InstanceFinder.SceneManager.AddConnectionToScene(client, scene);
            
            ClientNetworkedScene broadcast = new(scene.GetSceneLookupData(), ClientNetworkedSceneAction.ADD);
            InstanceFinder.ServerManager.Broadcast(client, broadcast);
            ClientNetworkedSceneEvent?.Invoke(client, scene.GetSceneLookupData(), ClientNetworkedSceneAction.ADD);
        }

#region Broadcasts
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
        /// Method call coming from a client to request a networked scene action.
        /// </summary>
        /// <param name="client">The client requesting the action</param>
        /// <param name="msg"></param>
        /// <param name="channel"></param>
        private void OnClientRequestNetworkedScene(NetworkConnection client, ClientNetworkedScene msg, Channel channel) 
        {
            if(msg.action == ClientNetworkedSceneAction.ADD) {
                AddClientToScene(client, msg.scene);
            } else {
                Debug.LogError("TODO: Implement RemoveClientFromScene");
            }
        }
#endregion

#region Events
        private void FishSceneManager_LoadEnd(SceneLoadEndEventArgs args)
        {
            foreach(Scene scene in args.LoadedScenes) {
                if(serverScenes.Contains(scene)) 
                    Debug.LogWarning("Fishnet loaded a scene even though it's already in the server scenes list.");

                serverScenes.Add(scene);
                ServerSceneListChangeEvent?.Invoke(scene.GetSceneLookupData(), serverScenes, true);
                BLog.Log($"FishNet SceneManager loaded scene {scene.GetSceneLookupData()}");

                if(InstanceFinder.IsHostStarted) {
                    BLog.Log($"  Host is started, treating FishNet SceneManager load as a client load {scene.GetSceneLookupData()}");
    
                    LoadSceneMode loadSceneMode = args.LoadedScenes.Count() == 1 ? LoadSceneMode.Single : LoadSceneMode.Additive;
                    ClientSceneChangeBroadcast broadcast = ClientSceneChangeBroadcast.LoadBroadcastFactory(LoadedScenes, scene.GetSceneLookupData(), loadSceneMode);
                    InstanceFinder.ClientManager.Broadcast(broadcast);

                    CheckClientLoadedScenes();
                }
            }
        }
#endregion
    }

    public static class SceneControllerExtensions 
    {
        public static SceneLookupData GetSceneLookupData(this Scene scene) => new(scene.handle, scene.name);
        public static SceneLookupData GetSceneLookupData(this UnloadedScene scene) => new(scene.Handle, scene.Name);
        public static Scene? Search(this IEnumerable<Scene> list, SceneLookupData target, bool allowNameOnly=false) 
        {
            // Try to perform relaxed search with name only if handle is zero
            if(target.Handle == 0 && allowNameOnly) {
                if(!list.Any(sld => sld.GetSceneLookupData().Name == target.Name))
                    return null;

                Scene firstResult = list.First(sld => sld.GetSceneLookupData().Name == target.Name);
                return firstResult;
            }

            if(!list.Any(scene => scene.GetSceneLookupData() == target))
                return null;

            return list.First(sld => sld.GetSceneLookupData() == target);
        }
    }
}