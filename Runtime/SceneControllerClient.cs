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

namespace EMullen.SceneMgmt 
{
    public partial class SceneController : MonoBehaviour 
    {

        /// <summary>
        /// For clients: The target list of scene lookup datas as specified by the server from a 
        ///   SceneSyncBroadcast.
        /// </summary>
        private List<SceneLookupData> clientTargetScenes;

        private void ClientOnEnable() 
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += UnitySceneManager_SceneLoaded;
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded += UnitySceneManager_SceneUnloaded;

            InstanceFinder.ClientManager.RegisterBroadcast<SceneSyncBroadcast>(OnSceneSync);
            InstanceFinder.ClientManager.RegisterBroadcast<ClientNetworkedScene>(OnClientNetworkedScene);
        }

        private void ClientOnDisable() 
        {
            InstanceFinder.ClientManager.UnregisterBroadcast<SceneSyncBroadcast>(OnSceneSync);
            InstanceFinder.ClientManager.UnregisterBroadcast<ClientNetworkedScene>(OnClientNetworkedScene);

            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= UnitySceneManager_SceneLoaded;
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded -= UnitySceneManager_SceneUnloaded;
        }

        /// <summary>
        /// Check if the client has loaded their target scenes. If so, invoke event and clear
        ///   target scenes.
        /// </summary>
        public void CheckClientLoadedScenes() 
        {
            if(LoadedScenes == null || clientTargetScenes == null)
                return;

            HashSet<SceneLookupData> loadedSet = LoadedScenes.ToHashSet();
            HashSet<SceneLookupData> targetSet = clientTargetScenes.ToHashSet();

            if(!LoadedScenes.ToHashSet().SetEquals(clientTargetScenes.ToHashSet()))
                return;

            LoadedTargetScenes?.Invoke(clientTargetScenes);
            clientTargetScenes = null;
            BLog.Log("Loaded target scenes locally.", logSettings, 1);
        }

#region Broadcasts
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

            BLog.Log($"Recieved scene sync broadcast, loading scenes: {string.Join(", ", clientTargetScenes)}", logSettings, 1);
        }

        /// <summary>
        /// Method call coming from the server to indicate to the client that they were 
        ///   added/removed from a networked scene.
        /// </summary>
        private void OnClientNetworkedScene(ClientNetworkedScene msg, Channel channel)
        {
            if(msg.action == ClientNetworkedSceneAction.ADD)
                UnityEngine.SceneManagement.SceneManager.SetActiveScene(UnityEngine.SceneManagement.SceneManager.GetSceneByName(msg.scene.Name));
        }
#endregion

#region Events
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

            CheckClientLoadedScenes();
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
#endregion
    }
}